using Fika.Core.Networking.LiteNetLib.Utils;

namespace Manimal.Terminal.Fika
{
    public struct TerminalPacket : INetSerializable
    {
        public const int Protocol = 2;
        public int Version;
        public byte Operation; // 0 request, 1 commit, 2 hello, 3 clock reply, 4 snapshot end, 5 plant grant, 6 pump shock
        public byte Kind;
        public string Raid, Key, Actor;
        public int Number;
        public long Revision;
        public double Time, Sent;
        public bool Replay;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Version); w.Put(Operation); w.Put(Kind);
            w.Put(Raid ?? ""); w.Put(Key ?? ""); w.Put(Actor ?? "");
            w.Put(Number); w.Put(Revision); w.Put(Time); w.Put(Sent); w.Put(Replay);
        }
        public void Deserialize(NetDataReader r)
        {
            Version = r.GetInt(); Operation = r.GetByte(); Kind = r.GetByte();
            Raid = r.GetString(); Key = r.GetString(); Actor = r.GetString();
            Number = r.GetInt(); Revision = r.GetLong(); Time = r.GetDouble(); Sent = r.GetDouble(); Replay = r.GetBool();
        }
    }
}
