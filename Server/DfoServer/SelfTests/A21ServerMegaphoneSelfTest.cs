using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using DfoServer.Game.Friends;
using DfoServer.Game.Inventory;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;

namespace DfoServer.SelfTests
{
    public static class A21ServerMegaphoneSelfTest
    {
        public static int Run()
        {
            int failed=0;
            void Check(string name,bool ok){Console.WriteLine($"[{(ok?"PASS":"FAIL")}] {name}");if(!ok)failed++;}
            try{RunAsync(Check).GetAwaiter().GetResult();}catch(Exception ex){Check(ex.ToString(),false);}
            Console.WriteLine($"A21_SERVER_MEGAPHONE: {failed} failure(s)");return failed==0?0:1;
        }

        private static async Task RunAsync(Action<string,bool> check)
        {
            using var db=new TestDatabase("server-megaphone");
            db.Database.Write((c,t)=>{using var cmd=c.CreateCommand();cmd.Transaction=t;
                cmd.CommandText="INSERT INTO accounts(account_id,m_id,password_hash,created_at) VALUES(100,'megaphone','',CURRENT_TIMESTAMP);"
                    +"INSERT INTO characters(character_id,account_id,name,level,created_at,updated_at) VALUES"
                    +"(701,100,'speaker',85,CURRENT_TIMESTAMP,CURRENT_TIMESTAMP),(702,100,'nearby',85,CURRENT_TIMESTAMP,CURRENT_TIMESTAMP),"
                    +"(703,100,'faraway',85,CURRENT_TIMESTAMP,CURRENT_TIMESTAMP),(704,100,'blocked',85,CURRENT_TIMESTAMP,CURRENT_TIMESTAMP);";
                cmd.ExecuteNonQuery();});
            var sessions=new SessionDirectory();var transitions=new CharacterTransitionCoordinator(sessions);
            using var sender=await Peer.Create(db.Database,sessions,701,10011);
            using var nearby=await Peer.Create(db.Database,sessions,702,10011);
            using var far=await Peer.Create(db.Database,sessions,703,10100);
            using var blocked=await Peer.Create(db.Database,sessions,704,10200);
            far.Session.Player.CurTownId=8;far.Session.Player.CurAreaId=3;
            // Collision with a UID on a different listener must not change the displayed sender.
            far.Session.Player.UserId=sender.Session.Player.UserId;
            var blacklist=new BlacklistRepository(db.Database);blacklist.Add(704,"speaker");
            using var handler=new ChatHandler(sessions,new PartyManager(),transitions);handler.ConfigureBlacklist(blacklist);
            InventoryLease Lease(){InventoryContext.TryGetOwnedLease(sender.Session.SessionId,701,out var lease);return lease;}
            int Count()=>Lease().Inventory.GetItem(InventoryListType.Main,65)?.Count??0;
            void Stock(int count,int expiry=0){var lease=Lease();lock(lease.SyncRoot)
                lease.Inventory.SetItem(InventoryListType.Main,65,new ItemCore{ItemKind=ItemCore.KindConsumable,ItemId=36,Count=count,ExpireTime=expiry});
                if(!InventoryPersistenceService.SaveDirty(lease))throw new Exception("test stock save failed");}
            Stock(3);
            var initialLease=Lease();lock(initialLease.SyncRoot)
                initialLease.Inventory.SetItem(InventoryListType.Main,66,new ItemCore{ItemKind=ItemCore.KindConsumable,ItemId=1,Count=3});
            if(!InventoryPersistenceService.SaveDirty(initialLease))throw new Exception("test alternate item save failed");
            var body=Body(65);var header=new GamePacketHeader{type=(ushort)CmdPacketTypeA21.SEND_MESSAGE};
            await handler.Handle_SEND_MESSAGE(sender.Session,header,Body(66));
            check("a different item cannot pay for a server megaphone",Count()==3
                && Lease().Inventory.GetItem(InventoryListType.Main,66).Count==3
                && nearby.Drain().Count==0 && far.Drain().Count==0);
            sender.Drain();
            await handler.Handle_SEND_MESSAGE(sender.Session,header,Body(67));sender.Drain();
            sender.Session.Player.Level=9;
            await handler.Handle_SEND_MESSAGE(sender.Session,header,body);
            check("missing source and PVF minimum-level rejection do not spend or broadcast",Count()==3
                && nearby.Drain().Count==0 && far.Drain().Count==0);
            sender.Session.Player.Level=85;
            await handler.Handle_SEND_MESSAGE(sender.Session,header,body.Concat(new byte[]{0}).ToArray());
            check("malformed megaphone request cannot consume",Count()==3 && sender.Drain().Count==0);
            await handler.Handle_SEND_MESSAGE(sender.Session,header,body);
            var own=sender.Drain();var other=nearby.Drain();var cross=far.Drain();
            check("one item is consumed and sender inventory refreshed",Count()==2
                && own.Any(p=>Type(p)==(ushort)NotiPacketTypeA21.ITEM_LIST));
            check("sender and peers in another area/channel receive the same name-bearing message",
                own.Count(IsMessage)==1 && other.Count(IsMessage)==1 && cross.Count(IsMessage)==1
                && other.Single(IsMessage).SequenceEqual(cross.Single(IsMessage))
                && cross.Single(IsMessage)[15]==11
                && BitConverter.ToInt32(cross.Single(IsMessage),17)=="speaker".Length
                && ClientTextEncoding.GetString(cross.Single(IsMessage).AsSpan(21,7).ToArray())=="speaker");
            check("blacklisted sender remains hidden",blocked.Drain().Count==0);
            using(var connection=db.OpenConnection())
                check("consumption survives a fresh inventory load",InventoryService.LoadFromDb(connection,701,100,db.Database)
                    .GetItem(InventoryListType.Main,65).Count==2);

            db.Database.Write((c,t)=>{using var cmd=c.CreateCommand();cmd.Transaction=t;
                cmd.CommandText="CREATE TRIGGER megaphone_fail BEFORE UPDATE ON character_inventory_items BEGIN SELECT RAISE(ABORT,'megaphone rollback test'); END;";cmd.ExecuteNonQuery();});
            await handler.Handle_SEND_MESSAGE(sender.Session,header,body);
            check("failed database commit restores item and publishes no message",Count()==2
                && !sender.Drain().Any(IsMessage) && nearby.Drain().Count==0 && far.Drain().Count==0);
            db.Database.Write((c,t)=>{using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="DROP TRIGGER megaphone_fail;";cmd.ExecuteNonQuery();});
            Stock(1);
            await Task.WhenAll(handler.Handle_SEND_MESSAGE(sender.Session,header,body),handler.Handle_SEND_MESSAGE(sender.Session,header,body));
            check("two concurrent requests cannot spend the final item twice",Count()==0
                && sender.Drain().Count(IsMessage)==1 && nearby.Drain().Count(IsMessage)==1 && far.Drain().Count(IsMessage)==1);
            await handler.Handle_SEND_MESSAGE(sender.Session,header,body);
            check("empty slot rejects a replay",Count()==0 && !sender.Drain().Any(IsMessage)
                && nearby.Drain().Count==0 && far.Drain().Count==0);
            Stock(1,(int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()-1);
            await handler.Handle_SEND_MESSAGE(sender.Session,header,body);
            check("expired source is removed without broadcasting",Count()==0 && !sender.Drain().Any(IsMessage)
                && nearby.Drain().Count==0 && far.Drain().Count==0);
            Stock(1);
            var blob=new byte[141];blob[0]=1;
            var linkedBody=Body(65,ClientTextEncoding.GetBytes("\u007F\u007F[测试装备]\u007F"));
            await handler.Handle_ITEM_HYPERLINK_MESSAGE(sender.Session,new GamePacketHeader{type=(ushort)CmdPacketTypeA21.ITEM_HYPERLINK_MESSAGE},linkedBody.Concat(blob).ToArray());
            var linked=far.Drain();nearby.Drain();sender.Drain();
            check("item-link megaphone also consumes and preserves tooltip bytes across channels",Count()==0
                && linked.Count==1 && Type(linked[0])==(ushort)NotiPacketTypeA21.MESSAGE_OTHER_CHANNEL_HYPER_LINK
                && linked[0].TakeLast(141).SequenceEqual(blob));
            Stock(1);
            using var replacement=await Peer.Create(db.Database,sessions,701,10011);
            await handler.Handle_SEND_MESSAGE(sender.Session,header,body);
            check("displaced sender cannot spend or publish",replacement.Inventory.GetItem(InventoryListType.Main,65).Count==1
                && nearby.Drain().Count==0 && far.Drain().Count==0 && sender.Drain().Count==0);
        }
        private static byte[] Body(uint slot,byte[] text=null){var w=new GamePacketWriter();w.WriteByte(11);w.WriteUInt16(0);w.WriteUInt32(slot);w.WriteDstr(text??ClientTextEncoding.GetBytes("喇叭测试123"));return w.ToArray();}
        private static ushort Type(byte[] p)=>BitConverter.ToUInt16(p,1);
        private static bool IsMessage(byte[] p)=>p[0]==0 && Type(p)==(ushort)NotiPacketTypeA21.MESSAGE_OTHER_CHANNEL;
        private sealed class TestDatabase:IDisposable
        {
            private readonly string _path;
            internal TestDatabase(string hint)
            {
                _path=Path.Combine(Path.GetTempPath(),$"dfo_st_{hint}_{Guid.NewGuid():N}.db");
                Database=new GameDatabase(_path,ServerPaths.SchemaFilePath);
            }
            internal GameDatabase Database{get;}
            internal Microsoft.Data.Sqlite.SqliteConnection OpenConnection()=>Database.OpenConnection();
            public void Dispose()
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach(var suffix in new[]{"","-wal","-shm"})
                    if(File.Exists(_path+suffix))File.Delete(_path+suffix);
            }
        }
        private sealed class Peer:IDisposable
        {
            private TcpClient _reader;
            internal EnhancedClientSession Session;
            internal InventoryService Inventory;
            internal static async Task<Peer> Create(IGameDatabase db,SessionDirectory sessions,int id,int port)
            {
                var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
                var reader=new TcpClient{ReceiveTimeout=3000};TcpClient writer;
                try{var connecting=reader.ConnectAsync(IPAddress.Loopback,((IPEndPoint)listener.LocalEndpoint).Port);
                    writer=await listener.AcceptTcpClientAsync();await connecting;}finally{listener.Stop();}
                var session=new EnhancedClientSession(writer,new GamePacketHeader(),port);
                session.Player.CharacterId=id;session.Player.UserId=(ushort)id;session.Player.Level=85;
                session.Player.Name=ClientTextEncoding.GetBytes(id==701?"speaker":"peer"+id);
                InventoryService inventory;using(var c=db.OpenConnection())inventory=InventoryService.LoadFromDb(c,id,100,db);
                InventoryContext.Register(session.SessionId,inventory);sessions.Register(id,session);
                return new Peer{_reader=reader,Session=session,Inventory=inventory};
            }
            internal List<byte[]> Drain(){var packets=new List<byte[]>();var stream=_reader.GetStream();
                while(_reader.Client.Poll(20000,SelectMode.SelectRead)){
                    if(_reader.Available==0)throw new Exception("test connection closed");
                    var h=new byte[15];stream.ReadExactly(h);int n=BitConverter.ToInt32(h,3);
                    if(n<15 || n>65536)throw new Exception("invalid packet length");
                    var p=new byte[n];h.CopyTo(p,0);stream.ReadExactly(p.AsSpan(15));packets.Add(p);}
                return packets;}
            public void Dispose(){InventoryContext.Unregister(Session.SessionId);Session.Close();_reader.Dispose();}
        }
    }
}
