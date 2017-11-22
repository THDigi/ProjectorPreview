using System;
using ProtoBuf;
using VRageMath;

namespace Digi.ProjectorPreview
{
    /// <summary>
    /// Used for serializing the settings.
    /// </summary>
    [ProtoContract]
    public class ProjectorPreviewModSettings
    {
        [ProtoMember(1)]
        public bool Enabled = false;

        [ProtoMember(2)]
        public bool Status = false;

        [ProtoMember(3)]
        public bool SeeThrough = false;

        [ProtoMember(4)]
        public float Scale = -1;

        [ProtoMember(5)]
        public Vector3 Offset = new Vector3(0, 0, 0);

        [ProtoMember(6)]
        public Vector3 RotateRad = new Vector3(0, 0, 0);

        [ProtoMember(7)]
        public SpinFlags Spin = SpinFlags.NONE;

        // blueprint is stored separately to avoid hitches when saving these tiny settings

        public override string ToString()
        {
            return $"Enabled = {Enabled}\nStatus = {Status}\nSeeThrough = {SeeThrough}\nScale = {Math.Round(Scale, 4)}\nOffset = {Math.Round(Offset.X, 4)}, {Math.Round(Offset.Y, 4)}, {Math.Round(Offset.Z, 4)}\nRotateRad = {Math.Round(RotateRad.X, 4)}, {Math.Round(RotateRad.Y, 4)}, {Math.Round(RotateRad.Z, 4)}\nSpin = {Spin}";
        }
    }

    [Flags]
    public enum SpinFlags : byte
    {
        NONE = 0,
        X = (1 << 0),
        Y = (1 << 1),
        Z = (1 << 2),
    };

    public static class SpinFlagsExtensions
    {
        public static bool IsFlagSet(this SpinFlags data, SpinFlags flag)
        {
            return (data & flag) != 0;
        }

        public static bool IsAxisSet(this SpinFlags data, int axis)
        {
            return (data & (SpinFlags)(1 << axis)) != 0;
        }
    }

    [ProtoContract]
    public class PacketData
    {
        [ProtoMember(1)]
        public PacketType Type = PacketType.SETTINGS;

        [ProtoMember(2)]
        public long EntityId = 0;

        [ProtoMember(3)]
        public ulong Sender = 0;

        [ProtoMember(4)]
        public ProjectorPreviewModSettings Settings = null;

        public PacketData() { } // empty ctor is required for deserialization

        public PacketData(ulong sender, long entityId, ProjectorPreviewModSettings settings)
        {
            Type = PacketType.SETTINGS;
            Sender = sender;
            EntityId = entityId;
            Settings = settings;
        }

        public PacketData(ulong sender, long entityId, PacketType action)
        {
            Type = action;
            Sender = sender;
            EntityId = entityId;
            Settings = null;
        }
    }

    public enum PacketType : byte
    {
        SETTINGS,
        REMOVE,
        RECEIVED_BP,
        USE_THIS_AS_IS,
        USE_THIS_FIX,
    }
}
