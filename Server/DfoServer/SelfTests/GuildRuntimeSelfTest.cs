using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using DfoServer.Game.Characters;
using DfoServer.Game.Guilds;
using DfoServer.Game.Inventory;
using DfoServer.Game.Party;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;

namespace DfoServer.SelfTests
{
    internal static class GuildRuntimeSelfTest
    {
        internal static async Task RunAsync(IGameDatabase database, Action<string, bool> check)
        {
            database.Write((c,t) =>
            {
                using var cmd=c.CreateCommand(); cmd.Transaction=t;
                cmd.CommandText="INSERT INTO characters(character_id,account_id,name) VALUES(201,100,'runtime-leader'),(202,100,'runtime-member'),(203,100,'runtime-outsider');";
                cmd.ExecuteNonQuery();
            });
            var repository=new GuildRepository(database);
            var guild=database.Write((c,t)=>GuildRepository.Insert(c,t,201,"runtime-guild",""));
            repository.Apply(guild.Id,202); repository.Approve(guild.Id,201,202);
            var sessions=new SessionDirectory();
            var transitions=new CharacterTransitionCoordinator(sessions);
            using var leader=await Peer.Create(database,sessions,201,10010);
            using var member=await Peer.Create(database,sessions,202,10011);
            using var outsider=await Peer.Create(database,sessions,203,10010);
            using var chat=new ChatHandler(sessions,new PartyManager(),transitions);
            chat.ConfigureGuilds(repository,transitions);
            var characters=new SqliteCharacterRepository(database);
            var appearance=new InventoryRefreshSender(new SqliteSelectCharacterDataSource(database,characters),characters,database);
            appearance.BindSessions(sessions);
            var publisher=new GuildStatePublisher(repository,transitions,sessions,appearance);
            var handler=new GuildManagementHandler(repository,transitions,publisher);
            var message=new GamePacketWriter();message.WriteByte(6);message.WriteUInt16(0);message.WriteUInt32(0);message.WriteClientDstr("公会测试");
            await chat.Handle_SEND_MESSAGE(leader.Session,new GamePacketHeader(),message.ToArray());
            check("guild chat crosses channels and excludes outsiders", leader.Drain().Count==1 && member.Drain().Count==1 && outsider.Drain().Count==0);
            await chat.Handle_SEND_MESSAGE(outsider.Session,new GamePacketHeader(),message.ToArray());
            check("nonmember cannot send guild chat or echo", leader.Drain().Count==0 && member.Drain().Count==0 && outsider.Drain().Count==0);

            var memo=new GamePacketWriter();memo.WriteClientDstr("在线备注");
            await handler.Handle(member.Session,new GamePacketHeader{type=(ushort)CmdPacketTypeA21.WRITE_GUILD_MEMBER_MEMO},memo.ToArray());
            check("own memo edit refreshes online guild members", repository.GetRosterForMember(202).Members.Single(m=>m.CharacterId==202).Memo=="在线备注"
                && leader.Drain().Any(p=>BitConverter.ToUInt16(p,1)==(ushort)CmdPacketTypeA21.GUILD_MEMER_LIST)
                && member.Drain().Any(p=>BitConverter.ToUInt16(p,1)==(ushort)CmdPacketTypeA21.GUILD_MEMER_LIST));
            var rank=new GamePacketWriter();rank.WriteClientDstr("runtime-member");rank.WriteByte(2);
            await handler.Handle(leader.Session,new GamePacketHeader{type=(ushort)CmdPacketTypeA21.SET_SUB_GUILD_MASTER},rank.ToArray());
            check("rank handler updates online recipient permissions", repository.GetRosterForMember(202).Members.Single(m=>m.CharacterId==202).Rank==2
                && member.Drain().Any(p=>p[0]==0 && BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.GUILD_MEMBER_INFO && p[15]==2));
            leader.Drain();
            // Wait on an existing transition, replace the sender generation, then
            // ensure the queued command cannot mutate the new connection's state.
            var gate=await transitions.AcquireAsync(201);
            var staleEdit=handler.Handle(leader.Session,new GamePacketHeader{type=(ushort)CmdPacketTypeA21.NOTIFY_MESSAGE_TO_GUILD},memo.ToArray());
            using var replacement=await Peer.Create(database,sessions,201,10010);
            gate.Dispose(); await staleEdit;
            check("queued stale generation cannot edit or refresh replacement", repository.GetForMember(201).Announcement==""
                && replacement.Drain().Count==0 && leader.Drain().Count==0);
            await chat.Handle_SEND_MESSAGE(leader.Session,new GamePacketHeader(),message.ToArray());
            check("replaced sender cannot broadcast guild chat", member.Drain().Count==0 && replacement.Drain().Count==0);
            await handler.Handle(member.Session,new GamePacketHeader{type=(ushort)CmdPacketTypeA21.REQ_GUILD_SECEDE},new byte[4]);
            check("leave clears current identity and refreshes survivor", !repository.IsMember(202) && member.Session.Player.Subtype0Tail.GuildId==0
                && member.Drain().Any(p=>p[0]==0 && BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.GUILD_SECEDE_TO_USER)
                && replacement.Drain().Any(p=>p[0]==1 && BitConverter.ToUInt16(p,1)==(ushort)CmdPacketTypeA21.GUILD_MEMER_LIST));
            await chat.Handle_SEND_MESSAGE(member.Session,new GamePacketHeader(),message.ToArray());
            check("former member immediately loses guild chat", replacement.Drain().Count==0 && member.Drain().Count==0);

            repository.Apply(guild.Id,202); repository.Approve(guild.Id,201,202);
            await publisher.RefreshAsync(new[]{201,202}); replacement.Drain(); member.Drain();
            var kick=new GamePacketWriter();kick.WriteClientDstr("runtime-member");
            var kickHeader=new GamePacketHeader{type=(ushort)CmdPacketTypeA21.REQ_GUILD_SECEDE};
            await handler.Handle(outsider.Session,kickHeader,kick.ToArray());
            check("outsider cannot expel a named member", repository.IsMember(202)
                && outsider.Drain().Any(p=>p[0]==1 && p[15]==0));
            var leaderName=new GamePacketWriter();leaderName.WriteClientDstr("runtime-leader");
            await handler.Handle(member.Session,kickHeader,leaderName.ToArray());
            check("member cannot expel named leader", repository.IsMember(201)
                && member.Drain().Any(p=>p[0]==1 && p[15]==0));
            await handler.Handle(replacement.Session,kickHeader,kick.ToArray().Concat(new byte[]{0}).ToArray());
            check("kick rejects trailing bytes without deleting membership", repository.IsMember(202)
                && replacement.Drain().Any(p=>p[0]==1 && p[15]==0));
            await handler.Handle(replacement.Session,kickHeader,kick.ToArray());
            check("named kick clears online target and refreshes leader", !repository.IsMember(202)
                && member.Session.Player.Subtype0Tail.GuildId==0
                && member.Drain().Any(p=>p[0]==0 && BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.GUILD_SECEDE_TO_USER)
                && replacement.Drain().Any(p=>p[0]==1 && BitConverter.ToUInt16(p,1)==(ushort)CmdPacketTypeA21.REQ_GUILD_SECEDE && p[15]==1 && p[16]==2));
            await handler.Handle(replacement.Session,kickHeader,kick.ToArray());
            check("repeated named kick fails without affecting leader", repository.IsMember(201) && !repository.IsMember(202)
                && replacement.Drain().Any(p=>p[0]==1 && p[15]==0) && member.Drain().Count==0);
            await chat.Handle_SEND_MESSAGE(member.Session,new GamePacketHeader(),message.ToArray());
            check("expelled member immediately loses guild chat", replacement.Drain().Count==0 && member.Drain().Count==0);

            repository.Apply(guild.Id,202); repository.Approve(guild.Id,201,202);
            await publisher.RefreshAsync(new[]{201,202}); replacement.Drain(); member.Drain();
            repository.Apply(guild.Id,203,"申请留言");
            await handler.Handle(outsider.Session,new GamePacketHeader{type=(ushort)CmdPacketTypeA21.JOIN_GUILD_INFO},Array.Empty<byte>());
            check("pending application is restored from durable state", outsider.Drain().Any(p=>p[0]==0
                && BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.JOIN_GUILD_INFO && p[15]==1));
            await handler.Handle(outsider.Session,new GamePacketHeader{type=(ushort)CmdPacketTypeA21.CANCEL_JOIN_GUILD},BitConverter.GetBytes(guild.Id));
            check("cancel application clears applicant UI immediately", repository.GetPendingApplication(203)==null
                && outsider.Drain().Any(p=>p[0]==0 && BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.JOIN_GUILD_INFO && p[15]==0));
            repository.Apply(guild.Id,203,"再次申请");
            await handler.Handle(replacement.Session,new GamePacketHeader{type=(ushort)CmdPacketTypeA21.DENY_JOIN_GUILD},BitConverter.GetBytes(203));
            check("denial clears online applicant UI", repository.GetPendingApplication(203)==null
                && outsider.Drain().Any(p=>p[0]==0 && BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.JOIN_GUILD_INFO && p[15]==0));
            replacement.Drain(); member.Drain();
            var announcement=new GamePacketWriter();announcement.WriteClientDstr("欢迎加入");
            await handler.Handle(replacement.Session,new GamePacketHeader{type=(ushort)CmdPacketTypeA21.NOTIFY_MESSAGE_TO_GUILD},announcement.ToArray());
            check("announcement persists and refreshes online members", new GuildRepository(database).Get(guild.Id).Announcement=="欢迎加入"
                && member.Drain().Any(p=>p[0]==0 && BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.GUILD_INFO));
            replacement.Drain();
            await handler.Handle(member.Session,new GamePacketHeader{type=(ushort)CmdPacketTypeA21.NOTIFY_MESSAGE_TO_GUILD},memo.ToArray());
            check("member cannot overwrite announcement", repository.Get(guild.Id).Announcement=="欢迎加入"
                && member.Drain().Any(p=>p[0]==1 && p[15]==0));
            await handler.Handle(replacement.Session,new GamePacketHeader{type=(ushort)CmdPacketTypeA21.MODIFY_GUILD_PROMOTE_MSG},announcement.ToArray());
            check("promotion update is visible in subsequent guild search", repository.FindByName("runtime-guild").Guild.Promotion=="欢迎加入");
            replacement.Drain(); member.Drain();
            var transfer=new GamePacketWriter();transfer.WriteClientDstr("runtime-member");transfer.WriteByte(1);
            await handler.Handle(replacement.Session,new GamePacketHeader{type=(ushort)CmdPacketTypeA21.SET_SUB_GUILD_MASTER},transfer.ToArray());
            check("rank request cannot activate excluded leadership transfer", repository.Get(guild.Id).LeaderId==201
                && replacement.Drain().Any(p=>p[0]==1 && p[15]==0));
            repository.Apply(guild.Id,203,"等待审批");
            await handler.Handle(member.Session,new GamePacketHeader{type=(ushort)CmdPacketTypeA21.BREAK_GUILD},Array.Empty<byte>());
            check("member cannot disband guild", repository.Get(guild.Id)!=null && repository.GetPendingApplication(203)!=null
                && member.Drain().Any(p=>p[0]==1 && p[15]==0));
            // Fail the initiator's ACK and first broadcast target while leaving
            // its generation current: all other durable state still needs publishing.
            replacement.Session.Stream.Dispose();
            await handler.Handle(replacement.Session,new GamePacketHeader{type=(ushort)CmdPacketTypeA21.BREAK_GUILD},Array.Empty<byte>());
            check("disband survives initiator disconnect and clears member identity", repository.Get(guild.Id)==null
                && member.Session.Player.Subtype0Tail.GuildId==0
                && member.Drain().Any(p=>p[0]==0 && BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.GUILD_DISMISS));
            check("disband clears online pending applicants after cascade", repository.GetPendingApplication(203)==null
                && outsider.Drain().Any(p=>p[0]==0 && BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.JOIN_GUILD_INFO && p[15]==0));
        }

        private sealed class Peer : IDisposable
        {
            private readonly TcpClient _reader;
            internal EnhancedClientSession Session { get; }
            private Peer(TcpClient reader, EnhancedClientSession session) { _reader=reader; Session=session; }
            internal static async Task<Peer> Create(IGameDatabase database,SessionDirectory sessions,int id,int port)
            {
                var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
                var reader=new TcpClient{ReceiveTimeout=3000}; TcpClient writer;
                try
                {
                    var connecting=reader.ConnectAsync(IPAddress.Loopback,((IPEndPoint)listener.LocalEndpoint).Port);
                    writer=await listener.AcceptTcpClientAsync();await connecting;
                }
                finally { listener.Stop(); }
                var session=new EnhancedClientSession(writer,new GamePacketHeader(),port);
                var record=new SqliteCharacterRepository(database).GetById(id);
                session.Player.CharacterId=id; session.Player.UserId=checked((ushort)id);session.Player.Name=record.Name;
                session.Player.Level=record.Level;session.Player.Job=record.Job;session.Player.GrowType=record.GrowType;
                session.Player.Subtype0Tail=new UserInfoMinimumTailSnapshot();
                GuildIdentityProjection.Apply(new GuildRepository(database).GetForMember(id),session.Player.Subtype0Tail);
                InventoryContext.Register(session.SessionId,new InventoryService(id,100,database));
                sessions.Register(id,session);
                return new Peer(reader,session);
            }
            internal List<byte[]> Drain()
            {
                var packets=new List<byte[]>();var stream=_reader.GetStream();
                while(_reader.Client.Poll(20000,SelectMode.SelectRead))
                {
                    if(_reader.Available==0) throw new InvalidOperationException("guild test connection closed");
                    var header=new byte[15];stream.ReadExactly(header);int length=BitConverter.ToInt32(header,3);
                    if(length<15 || length>65536) throw new InvalidOperationException("invalid guild packet length");
                    var packet=new byte[length];header.CopyTo(packet,0);stream.ReadExactly(packet.AsSpan(15));packets.Add(packet);
                }
                return packets;
            }
            public void Dispose() { InventoryContext.Unregister(Session.SessionId);Session.Close();_reader.Dispose(); }
        }
    }
}
