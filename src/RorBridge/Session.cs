using RoR2;

namespace V1Bridge.Ror
{
    /// <summary>Whether the bridge is on. It is on only while the local player is V1: picking V1 in character select
    /// starts ULTRAKILL (so it has loaded by the time the run begins), and a run counts as bridged while the local
    /// body is V1's. Any other survivor is plain Risk of Rain 2.</summary>
    internal static class Session
    {
        /// <summary>The local player has V1 picked, in the lobby or in a run.</summary>
        public static bool Wanted { get; private set; }
        /// <summary>A run is on and the local player's body is V1's.</summary>
        public static bool Active { get; private set; }
        static bool launchTried;

        public static void Tick()
        {
            var user = LocalUserManager.GetFirstLocalUser();
            var net = user != null ? user.currentNetworkUser : null;
            bool inLobby = PreGameController.instance != null, inRun = Run.instance != null;
            var v1 = V1Survivor.BodyIndex;
            bool wanted = v1 != BodyIndex.None && net != null && (inLobby || inRun) && net.bodyIndexPreference == v1;
            if (wanted != Wanted)
            {
                Wanted = wanted;
                Plugin.Log.LogInfo(wanted ? "V1 picked: bridge wanted" : "V1 not picked: bridge not wanted");
            }
            if (!inLobby && !inRun) launchTried = false;
            if (wanted && !launchTried && !UkLink.Connected)
            {
                // once per visit to the lobby: a failed start is reported, not retried every frame
                launchTried = true;
                UkLauncher.Launch();
            }
            var body = user != null ? user.cachedBody : null;
            bool active = inRun && body != null && body.bodyIndex == v1 && v1 != BodyIndex.None;
            if (active != Active)
            {
                Active = active;
                Plugin.Log.LogInfo(active ? "bridge ON: this run is V1's" : "bridge OFF");
            }
        }
    }
}
