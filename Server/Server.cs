﻿using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Shared;
using Shared.Packet;
using Shared.Packet.Packets;

namespace Server;

public class Server {
    public readonly List<Client> Clients = new List<Client>();
    public readonly Logger Logger = new Logger("Server");
    private readonly MemoryPool<byte> memoryPool = MemoryPool<byte>.Shared;
    public Func<Client, IPacket, bool>? PacketHandler = null!;
    public event Action<Client, ConnectPacket> ClientJoined = null!;

    public async Task Listen(CancellationToken? token = null) {
        Socket serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        serverSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        serverSocket.Bind(new IPEndPoint(IPAddress.Parse(Settings.Instance.Server.Address), Settings.Instance.Server.Port));
        serverSocket.Listen();

        Logger.Info($"Listening on {serverSocket.LocalEndPoint}");

        try {
            while (true) {
                Socket socket = token.HasValue ? await serverSocket.AcceptAsync(token.Value) : await serverSocket.AcceptAsync();
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.NoDelay, true);

                Logger.Warn($"Accepted connection for client {socket.RemoteEndPoint}");

                try {
                    if (Clients.Count == Constants.MaxClients) {
                        Logger.Warn("Turned away client due to max clients");
                        await socket.DisconnectAsync(false);
                        continue;
                    }

                    Task.Run(() => HandleSocket(socket));
                }
                catch {
                    // super ignore this
                }
            }
        }
        catch (OperationCanceledException) {
            // ignore the exception, it's just for closing the server
        }

        Logger.Info("Server closing");

        try {
            serverSocket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception) {
            // ignore
        }
        finally {
            serverSocket.Close();
        }

        Logger.Info("Server closed");
    }

    public static void FillPacket<T>(PacketHeader header, T packet, Memory<byte> memory) where T : struct, IPacket {
        Span<byte> data = memory.Span;

        MemoryMarshal.Write(data, ref header);
        packet.Serialize(data[Constants.HeaderSize..]);
    }

    // broadcast packets to all clients
    public delegate void PacketReplacer<in T>(Client from, Client to, T value); // replacer must send

    public async Task BroadcastReplace<T>(T packet, Client sender, PacketReplacer<T> packetReplacer) where T : struct, IPacket {
        foreach (Client client in Clients.Where(client => sender.Id != client.Id)) packetReplacer(sender, client, packet);
    }

    public async Task Broadcast<T>(T packet, Client sender) where T : struct, IPacket {
        IMemoryOwner<byte> memory = MemoryPool<byte>.Shared.RentZero(Constants.HeaderSize + packet.Size);
        PacketHeader header = new PacketHeader {
            Id = sender?.Id ?? Guid.Empty,
            Type = Constants.PacketMap[typeof(T)].Type,
            PacketSize = packet.Size
        };
        FillPacket(header, packet, memory.Memory);
        await Broadcast(memory, sender);
    }

    /// <summary>
    ///     Takes ownership of data and disposes once done.
    /// </summary>
    /// <param name="data">Memory owner to dispose once done</param>
    /// <param name="sender">Optional sender to not broadcast data to</param>
    public async Task Broadcast(IMemoryOwner<byte> data, Client? sender = null) {
        await Task.WhenAll(Clients.Where(c => c.Connected && c != sender).Select(client => client.Send(data.Memory, sender)));
        data.Dispose();
    }

    /// <summary>
    ///     Broadcasts memory whose memory shouldn't be disposed, should only be fired by server code.
    /// </summary>
    /// <param name="data">Memory to send to the clients</param>
    /// <param name="sender">Optional sender to not broadcast data to</param>
    public async void Broadcast(Memory<byte> data, Client? sender = null) {
        await Task.WhenAll(Clients.Where(c => c.Connected && c != sender).Select(client => client.Send(data, sender)));
    }

    public Client? FindExistingClient(Guid id) {
        return Clients.Find(client => client.Id == id);
    }


    private async void HandleSocket(Socket socket) {
        Client client = new Client(socket) {Server = this};
        IMemoryOwner<byte> memory = null!;
        bool first = true;
        try {
            while (true) {
                memory = memoryPool.Rent(Constants.HeaderSize);

                async Task<bool> Read(Memory<byte> readMem, int readSize = -1) {
                    int readOffset = 0;
                    if (readSize == -1) readSize = Constants.HeaderSize;
                    while (readOffset < readSize) {
                        int size = await socket.ReceiveAsync(readMem[readOffset..readSize], SocketFlags.None);
                        if (size == 0) {
                            // treat it as a disconnect and exit
                            Logger.Info($"Socket {socket.RemoteEndPoint} disconnected.");
                            if (socket.Connected) await socket.DisconnectAsync(false);
                            return false;
                        }

                        readOffset += size;
                    }

                    return true;
                }

                if (!await Read(memory.Memory[..Constants.HeaderSize])) break;
                PacketHeader header = GetHeader(memory.Memory.Span[..Constants.HeaderSize]);
                {
                    IMemoryOwner<byte> memTemp = memory;
                    memory = memoryPool.Rent(Constants.HeaderSize + header.PacketSize);
                    memTemp.Memory.CopyTo(memory.Memory);
                    memTemp.Dispose();
                }
                if (header.PacketSize > 0 
                    && !await Read(memory.Memory[Constants.HeaderSize..(Constants.HeaderSize + header.PacketSize)], header.PacketSize))
                    break;

                // connection initialization
                if (first) {
                    first = false;
                    if (header.Type != PacketType.Connect) throw new Exception($"First packet was not init, instead it was {header.Type}");

                    ConnectPacket connect = new ConnectPacket();
                    connect.Deserialize(memory.Memory.Span[Constants.HeaderSize..(Constants.HeaderSize + header.PacketSize)]);
                    lock (Clients) {
                        client.Name = connect.ClientName;
                        bool firstConn = false;
                        switch (connect.ConnectionType) {
                            case ConnectionTypes.FirstConnection: {
                                firstConn = true;
                                break;
                            }
                            case ConnectionTypes.Reconnecting: {
                                client.Id = header.Id;
                                if (FindExistingClient(header.Id) is { } newClient) {
                                    if (newClient.Connected) throw new Exception($"Tried to join as already connected user {header.Id}");
                                    newClient.Socket = client.Socket;
                                    client = newClient;
                                } else {
                                    firstConn = true;
                                    connect.ConnectionType = ConnectionTypes.FirstConnection;
                                }

                                break;
                            }
                            default:
                                throw new Exception($"Invalid connection type {connect.ConnectionType}");
                        }

                        client.Connected = true;
                        if (firstConn) {
                            // do any cleanup required when it comes to new clients
                            List<Client> toDisconnect = Clients.FindAll(c => c.Id == header.Id && c.Connected && c.Socket != null);
                            Clients.RemoveAll(c => c.Id == header.Id);

                            client.Id = header.Id;
                            Clients.Add(client);

                            Parallel.ForEachAsync(toDisconnect, (c, token) => c.Socket!.DisconnectAsync(false, token));
                            // done disconnecting and removing stale clients with the same id

                            ClientJoined?.Invoke(client, connect);
                        }
                    }

                    List<Client> otherConnectedPlayers = Clients.FindAll(c => c.Id != header.Id && c.Connected && c.Socket != null);
                    await Parallel.ForEachAsync(otherConnectedPlayers, async (other, _) => {
                        IMemoryOwner<byte> tempBuffer = MemoryPool<byte>.Shared.RentZero(Constants.HeaderSize + (other.CurrentCostume.HasValue ? Math.Max(connect.Size, other.CurrentCostume.Value.Size) : connect.Size));
                        PacketHeader connectHeader = new PacketHeader {
                            Id = other.Id,
                            Type = PacketType.Connect,
                            PacketSize = connect.Size
                        };
                        MemoryMarshal.Write(tempBuffer.Memory.Span, ref connectHeader);
                        ConnectPacket connectPacket = new ConnectPacket {
                            ConnectionType = ConnectionTypes.FirstConnection, // doesn't matter what it is :)
                            ClientName = other.Name
                        };
                        connectPacket.Serialize(tempBuffer.Memory.Span[Constants.HeaderSize..]);
                        await client.Send(tempBuffer.Memory[..(Constants.HeaderSize + connect.Size)], null);
                        if (other.CurrentCostume.HasValue) {
                            connectHeader.Type = PacketType.Costume;
                            connectHeader.PacketSize = other.CurrentCostume.Value.Size;
                            MemoryMarshal.Write(tempBuffer.Memory.Span, ref connectHeader);
                            other.CurrentCostume.Value.Serialize(tempBuffer.Memory.Span[Constants.HeaderSize..(Constants.HeaderSize + connectHeader.PacketSize)]);
                            await client.Send(tempBuffer.Memory[..(Constants.HeaderSize + connectHeader.PacketSize)], null);
                        }

                        tempBuffer.Dispose();
                    });

                    Logger.Info($"Client {client.Name} ({client.Id}/{socket.RemoteEndPoint}) connected.");
                } else if (header.Id != client.Id && client.Id != Guid.Empty) {
                    throw new Exception($"Client {client.Name} sent packet with invalid client id {header.Id} instead of {client.Id}");
                }

                if (header.Type == PacketType.Costume) {
                    CostumePacket costumePacket = new CostumePacket {
                        BodyName = ""
                    };
                    costumePacket.Deserialize(memory.Memory.Span[Constants.HeaderSize..(Constants.HeaderSize + costumePacket.Size)]);
                    client.CurrentCostume = costumePacket;
                }

                try {
                    // if (header.Type is not PacketType.Cap and not PacketType.Player) client.Logger.Warn($"lol {header.Type}");
                    IPacket packet = (IPacket) Activator.CreateInstance(Constants.PacketIdMap[header.Type])!;
                    packet.Deserialize(memory.Memory.Span[Constants.HeaderSize..(Constants.HeaderSize + packet.Size)]);
                    if (PacketHandler?.Invoke(client, packet) is false) {
                        memory.Dispose();
                        continue;
                    }
                }
                catch (Exception e) {
                    client.Logger.Error($"Packet handler warning: {e}");
                }

                Broadcast(memory, client);
            }
        }
        catch (Exception e) {
            if (e is SocketException {SocketErrorCode: SocketError.ConnectionReset}) {
                client.Logger.Info($"Client {socket.RemoteEndPoint} ({client.Id}) disconnected from the server");
            } else {
                client.Logger.Error($"Exception on socket {socket.RemoteEndPoint} ({client.Id}) and disconnecting for: {e}");
                if (socket.Connected) Task.Run(() => socket.DisconnectAsync(false));
            }

            memory?.Dispose();
        }

        Clients.Remove(client);
        client.Dispose();
        Task.Run(() => Broadcast(new DisconnectPacket(), client));
    }

    private static PacketHeader GetHeader(Span<byte> data) {
        //no need to error check, the client will disconnect when the packet is invalid :)
        return MemoryMarshal.Read<PacketHeader>(data);
    }
}