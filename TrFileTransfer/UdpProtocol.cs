using System;

namespace TrFileTransfer
{
    /// <summary>UDP wire protocol constants and packet helpers for Go-Back-N reliable transfer.</summary>
    public static class UdpProtocol
    {
        /// <summary>Magic number for packet identification (0x55445054 = "UDPT").</summary>
        public const int Magic = 0x55445054;
        /// <summary>Packet header size: magic(4) + type(1) + reserved(1) + seq(4) + bodyLen(4).</summary>
        public const int HeaderSize = 14;
        /// <summary>Maximum payload per data chunk (1400 bytes — fits in a single Ethernet frame without IP fragmentation).</summary>
        public const int MaxChunkSize = 1400;
        /// <summary>Default sliding window size in chunks (1024 × 1400 = 1.4 MB per window).</summary>
        public const int DefaultWindowSize = 1024;
        /// <summary>Default receive timeout in milliseconds (fallback before RTT measurement).</summary>
        public const int TimeoutMs = 3000;
        /// <summary>Minimum receive timeout in milliseconds (floor for dynamic RTT-based timeout).</summary>
        public const int MinTimeoutMs = 500;
        /// <summary>Maximum consecutive timeouts before aborting.</summary>
        public const int MaxRetries = 15;

        /// <summary>HELLO packet — initiates a new file transfer.</summary>
        public const byte TypeHello   = 0;
        /// <summary>DATA packet — carries a file chunk.</summary>
        public const byte TypeData    = 1;
        /// <summary>ACK packet — cumulative acknowledgement.</summary>
        public const byte TypeAck     = 2;
        /// <summary>FIN packet — signals end of file; body contains SHA256 hash.</summary>
        public const byte TypeFin     = 3;
        /// <summary>FIN_ACK packet — server confirms hash verification passed.</summary>
        public const byte TypeFinAck     = 4;
        /// <summary>FolderEnd packet — signals end of folder transfer.</summary>
        public const byte TypeFolderEnd = 5;
        /// <summary>NAK packet — requests retransmission from a specific seq (body: empty, seq = first missing chunk).</summary>
        public const byte TypeNak     = 6;

        /// <summary>Builds a complete UDP packet with header and optional body.</summary>
        public static byte[] BuildPacket(byte type, int sequence, byte[] body)
        {
            int bodyLen = body != null ? body.Length : 0;
            var packet = new byte[HeaderSize + bodyLen];
            Buffer.BlockCopy(BitConverter.GetBytes(Magic), 0, packet, 0, 4);
            packet[4] = type;
            packet[5] = 0; // reserved
            Buffer.BlockCopy(BitConverter.GetBytes(sequence), 0, packet, 6, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(bodyLen), 0, packet, 10, 4);
            if (body != null)
                Buffer.BlockCopy(body, 0, packet, HeaderSize, bodyLen);
            return packet;
        }

        /// <summary>Parses and validates a packet header. Returns false if magic, length, or bodyLen is invalid.</summary>
        public static bool ParseHeader(byte[] packet, out byte type, out int sequence, out int bodyLen)
        {
            type = 0;
            sequence = 0;
            bodyLen = 0;
            if (packet == null || packet.Length < HeaderSize)
                return false;
            int magic = BitConverter.ToInt32(packet, 0);
            if (magic != Magic)
                return false;
            type = packet[4];
            sequence = BitConverter.ToInt32(packet, 6);
            bodyLen = BitConverter.ToInt32(packet, 10);
            if (bodyLen < 0 || bodyLen > packet.Length - HeaderSize)
                return false;
            return true;
        }
    }
}
