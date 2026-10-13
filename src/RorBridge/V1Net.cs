using System;
using System.Collections.Generic;
using System.Globalization;
using RoR2;
using RoR2.Networking;
using UnityEngine;
using UnityEngine.Networking;

namespace V1Bridge.Ror
{
    /// <summary>V1 in someone else's game. Everything V1 does to Risk of Rain 2's world (hurting a monster, being hurt,
    /// its health, its death) is the host's to carry out: a joining player's game may not. So the joining V1's game
    /// tells the host what its ULTRAKILL did, and the host tells it what Risk of Rain 2 did to its V1, as lines of
    /// text over Risk of Rain 2's own connection (both games have the mod: Risk of Rain 2 only lets matching games
    /// play together). The player's picture, input, view, the stage under V1's feet and V1's movement are their own
    /// game's, as when hosting.
    ///   joining V1 -> host:  ENG 0|1 (my bridge has hold of my body), HP hp dead, DMG monster mult head explosion hitter x y z
    ///   host -> joining V1:  HURT n, HEAL x, CAP hp (as to ULTRAKILL: passed straight on), KILL monster, TP x y z
    /// Monsters are named by their network ids; positions are Risk of Rain 2's.
    /// Not carried over yet: parrying, the Whiplash dragging monsters, blast knockback, coins against hitscan.</summary>
    internal static class V1Net
    {
        const short ToHost = 27121, ToJoined = 27122;
        const string Engaged = "ENG";
        public static string Problem;
        /// <summary>For the tests.</summary>
        public static int SentToHost, SentToJoined, HostHits, HostHurts, JoinedKills;
        public static string LastHostHit = "";
        /// <summary>Every line that arrives goes to the log (the tests switch it on).</summary>
        public static bool Trace;
        static string lastTraced;

        sealed class Line : MessageBase
        {
            public string text;
            public override void Serialize(NetworkWriter writer) => writer.Write(text);
            public override void Deserialize(NetworkReader reader) => text = reader.ReadString();
        }

        static string S(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);
        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        public static void Hook()
        {
            try
            {
                NetworkManagerSystem.onStartServerGlobal += () => NetworkServer.RegisterHandler(ToHost, FromJoined);
                NetworkManagerSystem.onStartClientGlobal += client => client.RegisterHandler(ToJoined, FromHost);
                GlobalEventManager.onCharacterDeathGlobal += report =>
                {
                    // a monster died to a joining player's V1: their ULTRAKILL counts the kill
                    if (!NetworkServer.active || report.victimBody == null || report.attackerBody == null || V1HealthLink.Remotes.Count == 0) return;
                    var hc = report.attackerBody.healthComponent;
                    if (hc == null || !V1HealthLink.Remotes.TryGetValue(hc, out var link) || link.Remote == null) return;
                    var id = report.victimBody.GetComponent<NetworkIdentity>();
                    if (id != null) ToClient(link.Remote, Proto.Kill + " " + id.netId.Value);
                };
            }
            catch (Exception e)
            {
                Problem = e.Message;
                Plugin.Log.LogError("V1 in someone else's game: " + e);
            }
        }

        // ------------------------------------------------------------ the joining V1's game

        static uint engagedBody;
        static int sentHp = int.MinValue;
        static bool sentDead;
        static float sentAt;

        static void ToServer(string line)
        {
            var client = NetworkManagerSystem.singleton != null ? NetworkManagerSystem.singleton.client : null;
            if (client == null || client.connection == null || !client.isConnected) return;
            client.connection.SendByChannel(ToHost, new Line { text = line }, QosChannelIndex.defaultReliable.intVal);
            SentToHost++;
        }

        /// <summary>Every frame ULTRAKILL has hold of this game's V1 and this game is not the host: the host hears that
        /// the body is V1's to run, and what HP V1 has.</summary>
        public static void ClientTick(CharacterBody body, int hp, bool dead)
        {
            var id = body != null ? body.GetComponent<NetworkIdentity>() : null;
            if (id == null) return;
            if (engagedBody != id.netId.Value)
            {
                engagedBody = id.netId.Value;
                sentHp = int.MinValue;
                ToServer(Engaged + " 1");
                Plugin.Log.LogInfo("V1 is in someone else's game: the host carries out what it does");
            }
            if (hp != sentHp || dead != sentDead || Time.unscaledTime - sentAt > 0.5f)
            {
                sentHp = hp;
                sentDead = dead;
                sentAt = Time.unscaledTime;
                ToServer(Proto.Hp + " " + hp + " " + (dead ? 1 : 0));
            }
        }

        /// <summary>The bridge let go of this game's V1 (the run is over, the body is gone).</summary>
        public static void ClientLetGo()
        {
            if (engagedBody == 0) return;
            engagedBody = 0;
            if (!NetworkServer.active) ToServer(Engaged + " 0");
        }

        /// <summary>ULTRAKILL's V1 hit that monster: the host deals it.</summary>
        public static void ClientHit(CharacterBody victim, float mult, bool head, bool explosion, string hitter, Vector3 at)
        {
            var id = victim.GetComponent<NetworkIdentity>();
            if (id == null) return;
            ToServer(Proto.Damage + " " + id.netId.Value + " " + S(mult) + " " + (head ? 1 : 0) + " " + (explosion ? 1 : 0) + " " + hitter + " " + S(at.x) + " " + S(at.y) + " " + S(at.z));
        }

        static void FromHost(NetworkMessage msg)
        {
            string line = msg.ReadMessage<Line>().text ?? "";
            if (Trace) Plugin.Log.LogInfo("from the host: " + line);
            try
            {
                int sp = line.IndexOf(' ');
                string cmd = sp < 0 ? line : line.Substring(0, sp), rest = sp < 0 ? "" : line.Substring(sp + 1);
                switch (cmd)
                {
                    case Proto.Hurt:
                    case Proto.Heal:
                    case Proto.Cap:
                        // as the host's own V1 gets them: ULTRAKILL's to act on
                        if (cmd == Proto.Hurt) V1HealthLink.HurtsSent++;
                        UkLink.Send(line);
                        break;
                    case Proto.Kill:
                    {
                        var go = ClientScene.FindLocalObject(new NetworkInstanceId(uint.Parse(rest)));
                        var victim = go != null ? go.GetComponent<CharacterBody>() : null;
                        JoinedKills++;
                        if (victim != null) Monsters.Killed(victim);
                        break;
                    }
                    case Proto.Tp:
                    {
                        var a = rest.Split(' ');
                        V1Drive.OnRorTeleport(null, new Vector3(F(a[0]), F(a[1]), F(a[2])));
                        break;
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("from the host '" + line + "': " + e.Message); }
        }

        // ------------------------------------------------------------ the host

        public static void ToClient(NetworkConnection conn, string line)
        {
            if (conn == null || !conn.isConnected) return;
            conn.SendByChannel(ToJoined, new Line { text = line }, QosChannelIndex.defaultReliable.intVal);
            SentToJoined++;
        }

        /// <summary>The V1 body of the player on that connection, if that is what they are playing.</summary>
        static CharacterBody BodyOf(NetworkConnection conn)
        {
            foreach (var user in NetworkUser.readOnlyInstancesList)
            {
                if (user == null || user.connectionToClient != conn) continue;
                var body = user.GetCurrentBody();
                if (body != null && body.bodyIndex == V1Survivor.BodyIndex && V1Survivor.BodyIndex != BodyIndex.None) return body;
            }
            return null;
        }

        static void FromJoined(NetworkMessage msg)
        {
            string line = msg.ReadMessage<Line>().text ?? "";
            if (Trace && line != lastTraced) Plugin.Log.LogInfo("from a joining V1: " + (lastTraced = line));
            try
            {
                var body = BodyOf(msg.conn);
                var link = body != null ? body.GetComponent<V1HealthLink>() : null;
                if (link == null) return;
                var a = line.Split(' ');
                switch (a[0])
                {
                    case Engaged:
                        link.SetRemote(a.Length > 1 && a[1] == "1" ? msg.conn : null);
                        Plugin.Log.LogInfo("a joining player's V1 " + (link.Remote != null ? "is run by their ULTRAKILL" : "was let go of"));
                        break;
                    case Proto.Hp:
                        if (link.Remote == msg.conn && a.Length > 2) link.SetRemoteHp(int.Parse(a[1]), a[2] == "1");
                        break;
                    case Proto.Damage:
                    {
                        if (link.Remote != msg.conn || a.Length < 9) break;
                        var go = NetworkServer.FindLocalObject(new NetworkInstanceId(uint.Parse(a[1])));
                        var victim = go != null ? go.GetComponent<CharacterBody>() : null;
                        if (victim == null || victim == body || victim.healthComponent == null || !victim.healthComponent.alive) break;
                        // only what V1 could be fighting: not its own side
                        if (victim.teamComponent != null && body.teamComponent != null && victim.teamComponent.teamIndex == body.teamComponent.teamIndex) break;
                        // what one hit of ULTRAKILL's can be worth, whatever the line says
                        float mult = Mathf.Clamp(F(a[2]), 0f, 100f);
                        LastHostHit = Monsters.Hit(body, victim, mult, a[3] == "1", a[4] == "1", a[5], new Vector3(F(a[6]), F(a[7]), F(a[8])));
                        HostHits++;
                        break;
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("from a joining V1 '" + line + "': " + e.Message); }
        }

        /// <summary>Risk of Rain 2 (the host) teleported that body. True: it is a joining player's V1, and their game was told.</summary>
        public static bool ServerTeleported(CharacterBody body, Vector3 feet)
        {
            if (V1HealthLink.Remotes.Count == 0 || body.healthComponent == null || !V1HealthLink.Remotes.TryGetValue(body.healthComponent, out var link) || link.Remote == null) return false;
            ToClient(link.Remote, Proto.Tp + " " + S(feet.x) + " " + S(feet.y) + " " + S(feet.z));
            return true;
        }
    }
}
