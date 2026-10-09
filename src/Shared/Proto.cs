namespace V1Bridge
{
    /// <summary>What both plugins agree on. One UTF-8 line per message over TCP on loopback; UkBridge listens.
    /// Positions on the wire are ULTRAKILL's own coordinates unless a message says otherwise; RorBridge converts.</summary>
    internal static class Proto
    {
        public const string Version = "0.1.0";
        /// <summary>Bumped whenever a message or the frame file changes shape; the handshake refuses a mismatch.</summary>
        public const int Protocol = 2;
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
        public const string Idle = "IDLE";         // IDLE 0|1: Risk of Rain 2 is not in a V1 run; ULTRAKILL stops drawing frames

        // level (ULTRAKILL -> RoR2)
        public const string Loading = "LOADING";   // LOADING <scene>: not ready yet, and where it is
        public const string Ready = "READY";       // the Sandbox is stripped down to V1 and waiting for a TP
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
