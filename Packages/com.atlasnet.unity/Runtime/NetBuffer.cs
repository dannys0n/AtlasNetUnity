using System;
using System.IO;
using UnityEngine;

namespace AtlasNet
{
    public sealed class NetWriter : IDisposable
    {
        private readonly MemoryStream stream = new MemoryStream();
        private readonly BinaryWriter writer;
        public NetWriter() => writer = new BinaryWriter(stream);
        public void Write(byte value) => writer.Write(value);
        public void Write(bool value) => writer.Write(value);
        public void Write(ushort value) => writer.Write(value);
        public void Write(int value) => writer.Write(value);
        public void Write(uint value) => writer.Write(value);
        public void Write(ulong value) => writer.Write(value);
        public void Write(float value) => writer.Write(value);
        public void Write(string value) => writer.Write(value ?? string.Empty);
        public void Write(EntityId value) => writer.Write(value.Value);
        public void Write(SessionId value) => writer.Write(value.Value);
        public void Write(Vector3 value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
        }
        public void Write(Quaternion value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
            writer.Write(value.w);
        }
        public void WriteBytes(byte[] value)
        {
            writer.Write(value.Length);
            writer.Write(value);
        }
        public byte[] ToArray() => stream.ToArray();
        public void Dispose()
        {
            writer.Dispose();
            stream.Dispose();
        }
    }

    public sealed class NetReader : IDisposable
    {
        private readonly MemoryStream stream;
        private readonly BinaryReader reader;
        public NetReader(byte[] data)
        {
            stream = new MemoryStream(data, false);
            reader = new BinaryReader(stream);
        }
        public bool HasRemaining => stream.Position < stream.Length;
        public byte ReadByte() => reader.ReadByte();
        public bool ReadBool() => reader.ReadBoolean();
        public ushort ReadUShort() => reader.ReadUInt16();
        public int ReadInt() => reader.ReadInt32();
        public uint ReadUInt() => reader.ReadUInt32();
        public ulong ReadULong() => reader.ReadUInt64();
        public float ReadFloat() => reader.ReadSingle();
        public string ReadString() => reader.ReadString();
        public EntityId ReadEntityId() => new EntityId(reader.ReadUInt64());
        public SessionId ReadSessionId() => new SessionId(reader.ReadUInt64());
        public Vector3 ReadVector3() => new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        public Quaternion ReadQuaternion() => new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        public byte[] ReadBytes()
        {
            int size = reader.ReadInt32();
            if (size < 0 || size > 1024 * 1024 || size > stream.Length - stream.Position)
                throw new InvalidDataException($"Invalid payload size {size}");
            return reader.ReadBytes(size);
        }
        public void Dispose()
        {
            reader.Dispose();
            stream.Dispose();
        }
    }

    internal static class NetValueCodec<T>
    {
        public static void Write(NetWriter writer, T value)
        {
            object boxed = value;
            if (typeof(T) == typeof(int)) writer.Write((int)boxed);
            else if (typeof(T) == typeof(float)) writer.Write((float)boxed);
            else if (typeof(T) == typeof(bool)) writer.Write((bool)boxed);
            else if (typeof(T) == typeof(string)) writer.Write((string)boxed);
            else if (typeof(T) == typeof(Vector3)) writer.Write((Vector3)boxed);
            else if (typeof(T) == typeof(Quaternion)) writer.Write((Quaternion)boxed);
            else throw new NotSupportedException($"NetworkVariable<{typeof(T).Name}> is not supported");
        }

        public static T Read(NetReader reader)
        {
            object value;
            if (typeof(T) == typeof(int)) value = reader.ReadInt();
            else if (typeof(T) == typeof(float)) value = reader.ReadFloat();
            else if (typeof(T) == typeof(bool)) value = reader.ReadBool();
            else if (typeof(T) == typeof(string)) value = reader.ReadString();
            else if (typeof(T) == typeof(Vector3)) value = reader.ReadVector3();
            else if (typeof(T) == typeof(Quaternion)) value = reader.ReadQuaternion();
            else throw new NotSupportedException($"NetworkVariable<{typeof(T).Name}> is not supported");
            return (T)value;
        }
    }
}
