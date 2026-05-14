using System;

namespace TrFileTransfer
{
    public static class UdpProtocol
    {
        public const int Magic = 0x55445054;
        public const int HeaderSize = 14;   // magic(4) + type(1) + reserved(1) + seq(4) + bodyLen(4)
        public const int MaxChunkSize = 32768;  // 32 KB, safe for all networks
        public const int DefaultWindowSize = 32;
        public const int TimeoutMs = 3000;
        public const int MaxRetries = 15;

        public const byte TypeHello   = 0;
        public const byte TypeData    = 1;
        public const byte TypeAck     = 2;
        public const byte TypeFin     = 3;
        public const byte TypeFinAck     = 4;
        public const byte TypeFolderEnd = 5;

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
