namespace V1Bridge
{
    /// <summary>What both plugins agree on. One UTF-8 line per message over TCP on loopback; UkBridge listens.
    /// Positions on the wire are ULTRAKILL's own coordinates unless a message says otherwise; RorBridge converts.</summary>
    internal static class Proto
    {
        public const string Version = "0.1.0";
        /// <summary>Bumped whenever a message or the frame file changes shape; the handshake refuses a mismatch.</summary>
        public const int Protocol = 7;
        /// <summary>Not Ultracraft's 27110, so both can be installed side by side.</summary>
        public const int DefaultPort = 27120;

        // command-line switches (ULTRAKILL)
        public const string UkArgActive = "-v1bridge";
        public const string UkArgPort = "-v1bridge-port";
        public const string UkArgParent = "-v1bridge-parent";

        // link
        public const string Hello = "HELLO";       // HELLO <protocol> <side> <version> [key=value ...]
        public const string Ping = "PING";         // PING <token>
        public const string Pong = "PONG";         // PONG <token>
        public const string State = "STATE";       // STATE -> STATE key=value ...
        public const string Snap = "SNAP";         // SNAP <path> -> SNAPPED <path> | SNAPFAIL <reason>
        public const string Snapped = "SNAPPED";
        public const string SnapFail = "SNAPFAIL";
        public const string Quit = "QUIT";

        // level (RoR2 -> ULTRAKILL)
        public const string Size = "SIZE";         // SIZE <w> <h> <fps cap>: what ULTRAKILL renders at
        public const string Tp = "TP";             // TP <x> <y> <z> <yaw> <pitch>: V1's feet, ULTRAKILL coordinates
        public const string Input = "IN";          // IN <seq> <sent at, QPC microseconds> <dx> <dy> <wheel> <buttons> <KeyCode>...
        public const string Geo = "GEO";           // GEO <path>: the stage's colliders (StageGeometry's file), replacing any before -> GEODONE
        public const string GeoClear = "GEOCLR";   // the stage is gone
        public const string Probe = "PROBE";       // PROBE <x> <y> <z> -> PROBED <ground y below that point> <collider> | PROBED none
        public const string Ents = "ENTS";         // ENTS id,x,y,z,w,h,hp,light,wet;...: every monster alive (feet, width, height, ULTRAKILL damage units left, 1 if the Whiplash pulls it to V1, 1 if it stands in water)
        public const string Hurt = "HURT";         // HURT <n>: V1 loses n of ULTRAKILL's 100, if ULTRAKILL agrees (dash invincibility...)
        public const string Heal = "HEAL";         // HEAL <x>: V1 gains x of ULTRAKILL's 100
        public const string Kill = "KILL";         // KILL <id>: that monster died to V1
        public const string Projs = "PROJS";       // PROJS id,x,y,z,radius;...: every hostile projectile in flight
        public const string Parried = "PARRIED";   // PARRIED <monster id>: V1's punch met that monster's melee hit
        public const string Wet = "WET";           // WET 0|1: V1 stands in Risk of Rain 2's water (electricity in the water reaches V1 too)
        public const string Launch = "LAUNCH";     // LAUNCH <vx> <vy> <vz> <gravity>: a jump pad threw V1 (ULTRAKILL units/s as RoR2 meant it, under RoR2's gravity in units/s2)
        public const string Respawn = "RESPAWN";   // V1 alive and whole again (a new run, a revive)
        public const string TestCoin = "TESTCOIN"; // TESTCOIN <x> <y> <z>: (tests) one of ULTRAKILL's own coins, hanging still at that point
        public const string Idle = "IDLE";         // IDLE 0|1: Risk of Rain 2 is not in a V1 run; ULTRAKILL stops drawing frames

        // level (ULTRAKILL -> RoR2)
        public const string Loading = "LOADING";   // LOADING <scene>: not ready yet, and where it is
        public const string Ready = "READY";       // the Sandbox is stripped down to V1 and waiting for a TP
        public const string GeoDone = "GEODONE";   // GEODONE <colliders> <triangles> <ms> <ok|problem>
        public const string Probed = "PROBED";
        public const string Knock = "KNOCK";       // KNOCK <id> <dx> <dy> <dz> <strength>: a blast threw that monster this way (strength 1 = a rocket's)
        public const string Oiled = "OILED";       // OILED <id> <0-10>: how soaked in gasoline that monster is (0: clean again)
        public const string Model = "MODEL";       // MODEL <path> <info> | MODEL none: V1's body, read out of this ULTRAKILL, for character select
        public const string Pull = "PULL";         // PULL <id> <x> <y> <z>: the Whiplash is dragging that monster; its feet are here now
        public const string PullEnd = "PULLEND";   // PULLEND <id> <vx> <vy> <vz>: let go, moving like this
        public const string Parry = "PARRY";       // PARRY p <projectile id> <dir xyz>: V1's fist caught that projectile
        public const string Damage = "DMG";        // DMG <id> <multiplier> <head 0|1> <explosion 0|1> <hitter> <x> <y> <z>: V1 hit that monster
        public const string Landed = "LANDED";     // V1 is switched on (movement, camera, weapons, HUD)
        public const string Pose = "P";            // P <feet xyz> <eye xyz> <yaw> <pitch> <roll> <fov> <vel xyz> <grounded> <sliding>
        public const string Hp = "HP";             // HP <hp> <dead 0|1>

        // ---- frame file: %TEMP%\v1bridge_frame.bin, written by ULTRAKILL, read by Risk of Rain 2
        public const string FrameFile = "v1bridge_frame.bin";
        /// <summary>Named event set each time a frame is published.</summary>
        public const string FrameEvent = "v1bridge_frame_ready";
        public const int FrameHeader = 128;
        public const int FrameMaxPixels = 2560 * 1440;
        public const int FrameSlots = 4;
        /// <summary>Each slot: colour (RGBA32) then mask (RGBA32 of the pre-post-processing target), bottom row first.</summary>
        public const long FrameSlotBytes = FrameMaxPixels * 8L;
        public const int FrameVersion = 1;
        // header ints: 0 seq, 1 w, 2 h, 3 slot, 4 version, 5 input seq the frame was drawn with; longs at byte 32: drawn at
        // (QPC microseconds), 40: when that input was sent; floats at byte 48: eye xyz, yaw, pitch, roll, fov; int at 76: pose valid
        public const int HdrSeq = 0, HdrW = 1, HdrH = 2, HdrSlot = 3, HdrVersion = 4, HdrInputSeq = 5;
        public const int HdrDrawnAtByte = 32, HdrInputAtByte = 40, HdrPoseByte = 48, HdrPoseValidByte = 76;
    }
}
