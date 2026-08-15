using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DfoServer.Network
{
    
    public interface IPacketHeader
    {
        
        int GetHeaderSize();
        
        byte[] GetBytes();
        
        void ParseHeader(byte[] data);
        
        uint GetPacketLength();
    }

    
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ChannelPacketHeader : IPacketHeader
    {
        public byte classification;    
        public byte msg_no;           
        public uint sLength;          
        public uint check_sum;        
        public byte ack;              

        byte[] IPacketHeader.GetBytes()
        {
            int size = Marshal.SizeOf<ChannelPacketHeader>();
            byte[] arr = new byte[size];
            IntPtr ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(this, ptr, true);
            Marshal.Copy(ptr, arr, 0, size);
            Marshal.FreeHGlobal(ptr);
            return arr;
        }

        int IPacketHeader.GetHeaderSize()
        {
            return Marshal.SizeOf<ChannelPacketHeader>();
        }

        void IPacketHeader.ParseHeader(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                classification = reader.ReadByte();
                msg_no = reader.ReadByte();
                sLength = reader.ReadUInt32();
                check_sum = reader.ReadUInt32();
                ack = reader.ReadByte();
            }
        }

        uint IPacketHeader.GetPacketLength()
        {
            return sLength;
        }
    }

    
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct GamePacketHeader : IPacketHeader
    {
        public byte cmd;           
        public ushort type;        
        public uint length;        
        public uint checksum;      
        public ushort seq;         
        public byte extra;

        byte[] IPacketHeader.GetBytes()
        {
            int size = Marshal.SizeOf<GamePacketHeader>();
            byte[] arr = new byte[size];
            IntPtr ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(this, ptr, true);
            Marshal.Copy(ptr, arr, 0, size);
            Marshal.FreeHGlobal(ptr);
            return arr;
        }

        int IPacketHeader.GetHeaderSize()
        {
            return Marshal.SizeOf<GamePacketHeader>();
        }

        uint IPacketHeader.GetPacketLength()
        {
            return length;
        }

        void IPacketHeader.ParseHeader(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                cmd = reader.ReadByte();
                type = reader.ReadUInt16();
                length = reader.ReadUInt32();
                checksum = reader.ReadUInt32();
                seq = reader.ReadUInt16();
                extra = reader.ReadByte();
            }
        }
    }
}
