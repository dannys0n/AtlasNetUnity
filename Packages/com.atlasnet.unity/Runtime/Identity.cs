using System;

namespace AtlasNet
{
    /// <summary>A stable runtime identity. It does not encode a connection or worker.</summary>
    public readonly struct EntityId : IEquatable<EntityId>
    {
        public readonly ulong Value;
        public EntityId(ulong value) => Value = value;
        public bool Equals(EntityId other) => Value == other.Value;
        public override bool Equals(object other) => other is EntityId id && Equals(id);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value.ToString();
        public static bool operator ==(EntityId left, EntityId right) => left.Equals(right);
        public static bool operator !=(EntityId left, EntityId right) => !left.Equals(right);
    }

    /// <summary>Logical controlling session, distinct from a transport connection.</summary>
    public readonly struct SessionId : IEquatable<SessionId>
    {
        public readonly ulong Value;
        public SessionId(ulong value) => Value = value;
        public bool Equals(SessionId other) => Value == other.Value;
        public override bool Equals(object other) => other is SessionId id && Equals(id);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value.ToString();
        public static bool operator ==(SessionId left, SessionId right) => left.Equals(right);
        public static bool operator !=(SessionId left, SessionId right) => !left.Equals(right);
    }
}
