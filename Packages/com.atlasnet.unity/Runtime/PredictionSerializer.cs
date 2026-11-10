using UnityEngine;

namespace AtlasNet
{
    /// <summary>Defines which fields of a prediction input or state are sent over the network.</summary>
    public interface IPredictionData
    {
        void Serialize(PredictionSerializer serializer);
    }

    /// <summary>Reads or writes the same fields in the same order on every peer.</summary>
    public readonly struct PredictionSerializer
    {
        private readonly NetWriter writer;
        private readonly NetReader reader;

        internal PredictionSerializer(NetWriter writer) { this.writer = writer; reader = null; }
        internal PredictionSerializer(NetReader reader) { this.reader = reader; writer = null; }

        public void Serialize(ref bool value) { if (writer != null) writer.Write(value); else value = reader.ReadBool(); }
        public void Serialize(ref byte value) { if (writer != null) writer.Write(value); else value = reader.ReadByte(); }
        public void Serialize(ref int value) { if (writer != null) writer.Write(value); else value = reader.ReadInt(); }
        public void Serialize(ref uint value) { if (writer != null) writer.Write(value); else value = reader.ReadUInt(); }
        public void Serialize(ref float value) { if (writer != null) writer.Write(value); else value = reader.ReadFloat(); }
        public void Serialize(ref Vector2 value) { Serialize(ref value.x); Serialize(ref value.y); }
        public void Serialize(ref Vector3 value) { if (writer != null) writer.Write(value); else value = reader.ReadVector3(); }
        public void Serialize(ref Quaternion value) { if (writer != null) writer.Write(value); else value = reader.ReadQuaternion(); }
    }
}
