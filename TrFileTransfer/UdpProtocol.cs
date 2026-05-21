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
        /// <summary>Maximum payload per data chunk (4096 bytes — ~3 IP fragments, balance of throughput vs loss amplification).</summary>
        public const int MaxChunkSize = 4096;
        /// <summary>Default sliding window size in chunks (512 × 4096 = 2 MB per window).</summary>
        public const int DefaultWindowSize = 512;
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
            return BuildPacketFromBuffer(type, sequence, body, 0, body != null ? body.Length : 0);
        }

        /// <summary>Builds a UDP packet with body data sourced from a buffer at offset (avoids intermediate copy).</summary>
        public static byte[] BuildPacketFromBuffer(byte type, int sequence, byte[] source, int sourceOffset, int sourceLen)
        {
            var packet = new byte[HeaderSize + sourceLen];
            // Write magic, type, reserved, seq, bodyLen manually — avoids 3x BitConverter.GetBytes allocations
            packet[0] = 0x54;
            packet[1] = 0x50;
            packet[2] = 0x44;
            packet[3] = 0x55;
            packet[4] = type;
            packet[5] = 0;
            packet[6] = (byte)sequence;
            packet[7] = (byte)(sequence >> 8);
            packet[8] = (byte)(sequence >> 16);
            packet[9] = (byte)(sequence >> 24);
            packet[10] = (byte)sourceLen;
            packet[11] = (byte)(sourceLen >> 8);
            packet[12] = (byte)(sourceLen >> 16);
            packet[13] = (byte)(sourceLen >> 24);
            if (source != null && sourceLen > 0)
                Buffer.BlockCopy(source, sourceOffset, packet, HeaderSize, sourceLen);
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
