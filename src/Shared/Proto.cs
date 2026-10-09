namespace V1Bridge
{
    /// <summary>What both plugins agree on. One UTF-8 line per message over TCP on loopback; UkBridge listens.</summary>
    internal static class Proto
    {
        public const string Version = "0.1.0";
        /// <summary>Bumped whenever a message changes shape; the handshake refuses a mismatch.</summary>
        public const int Protocol = 1;
        /// <summary>Not Ultracraft's 27110, so both can be installed side by side.</summary>
        public const int DefaultPort = 27120;

        // command-line switches (ULTRAKILL)
        public const string UkArgActive = "-v1bridge";
        public const string UkArgPort = "-v1bridge-port";
        public const string UkArgParent = "-v1bridge-parent";

        // messages
        public const string Hello = "HELLO";       // HELLO <protocol> <side> <version> [key=value ...]
        public const string Ping = "PING";         // PING <token>
        public const string Pong = "PONG";         // PONG <token>
        public const string State = "STATE";       // STATE -> STATE key=value ...
        public const string Snap = "SNAP";         // SNAP <path> -> SNAPPED <path> | SNAPFAIL <reason>
        public const string Snapped = "SNAPPED";
        public const string SnapFail = "SNAPFAIL";
        public const string Quit = "QUIT";
    }
}
