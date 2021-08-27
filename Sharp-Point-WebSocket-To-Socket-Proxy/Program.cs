using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Sharp_Point_WebSocket_To_Socket_Proxy
{
    class Program
    {
        public static ManualResetEvent Manual { get; set; } = new(false);
        public const int PORT = 8094;
        public const string PRICE_IP = "192.168.123.136";
        public const int PRICE_PORT = 8093;

        static void Main(string[] args)
        {
            try
            {
                Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.DontLinger, true);
                socket.Bind(new IPEndPoint(IPAddress.Any, PORT));
                socket.Listen(256);
                while (true)
                {
                    _ = Manual.Reset();
                    _ = socket.BeginAccept(new AsyncCallback(OnAcceptWebSocket), new State() { WorkingSocket = socket });
                    Log($"Waiting for connection of web client on port {PORT}....");
                    _ = Manual.WaitOne();
                }
            }
            catch (Exception e)
            {
                Log(e.Message);
                Log(e.StackTrace);
                Log("");
                Log("Press any key to exit...");
                Console.ReadKey();
            }
        }

        private static void OnAcceptWebSocket(IAsyncResult result)
        {
            byte[] buffer = new byte[1024];
            State state = (State)result.AsyncState;
            Socket socket = state.WorkingSocket;
            try
            {
                Socket client = null;
                string handShakeRequest = "";
                if (socket != null && socket.IsBound)
                {
                    client = socket.EndAccept(result);
                    int size = client.Receive(buffer);
                    handShakeRequest = Encoding.Default.GetString(buffer, 0, size);
                }
                if (client != null)
                {
                    Log($"Accepted connection from {client.GetClientIPAddress()}");
                    string acceptKey = Utility.GenerateAcceptKey(Utility.GetWebSocketKey(handShakeRequest));
                    //string response = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {acceptKey}\r\nSec-WebSocket-Extensions: permessage-deflate; client_max_window_bits=15\r\n\r\n";
                    string response = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {acceptKey}\r\n\r\n";
                    int sent = client.Send(response.ToByteArray());

                    _ = Manual.Set();

                    state.PriceServerSocket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    state.PriceServerSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.DontLinger, true);
                    StartPriceServerClient(state.PriceServerSocket);
                    State clientState = new()
                    {
                        WorkingSocket = client,
                        PriceServerSocket = state.PriceServerSocket
                    };
                    EndPoint ep = client.RemoteEndPoint;
                    _ = client.BeginReceiveFrom(clientState.Buffer, 0, State.BUFFER_SIZE, SocketFlags.None, ref ep, new AsyncCallback(OnReceive), clientState);
                }
            }
            catch (SocketException e)
            {
                Log(e.Message);
                Log(e.StackTrace);
            }
            finally
            {
                if (socket != null && socket.IsBound)
                {
                    //_ = socket.BeginAccept(null, 0, OnAcceptWebSocket, null);
                }
            }
        }
        private static void OnReceive(IAsyncResult result)
        {
            State state = (State)result.AsyncState;
            Socket client = state.WorkingSocket;
            Socket priceClient = state.PriceServerSocket;
            EndPoint ep = client.RemoteEndPoint;
            Queue<byte[]> tempMessageQueue = new();
            string clientAddress = client.GetClientIPAddress();

            try
            {
                int size = client.EndReceiveFrom(result, ref ep);
                if (size > 0)
                {
                    byte[] message = state.Buffer.TrimEnd().DecodeWebsocketFrame(state).SelectMany(i => i).ToArray();
                    string messageString = message.ConvertToString();
                    Log($"message from {clientAddress}: {messageString}, length: {message.Length}, size: {size}");
                    Array.Clear(state.Buffer, 0, state.Buffer.Length);
                    if (client.Connected)
                    {
                        try
                        {
                            if (priceClient.Connected)
                            {
                                if (messageString.StartsWith("4104"))
                                {
                                    int sizePrice = priceClient.Send(message);
                                    Log($"sent message to price server: {message.ConvertToString()}, length: {message.ConvertToString().Length}, size: {sizePrice}");
                                }
                                else
                                {
                                    string[] mc = Regex.Split(messageString, @"41");
                                    if (mc.Length >= 1)
                                    {
                                        foreach (string m in mc)
                                        {
                                            if (string.IsNullOrEmpty(m))
                                            {
                                                continue;
                                            }
                                            string msg = $"41{m}{Environment.NewLine}";
                                            tempMessageQueue.Enqueue(msg.ToByteArray());
                                        }
                                        System.Timers.Timer timer = new(50);
                                        timer.Elapsed += (sender, e) =>
                                        {
                                            if (tempMessageQueue.Count != 0)
                                            {
                                                byte[] msg = tempMessageQueue.Dequeue();
                                                int sizePrice = priceClient.Send(msg);
                                                Log($"sent message to price server: {msg.ConvertToString()}, length: {msg.ConvertToString().Length}, size: {sizePrice}");
                                            }
                                        };
                                        timer.Start();
                                        while (tempMessageQueue.Count != 0)
                                        {

                                        }
                                        timer.Stop();
                                        Log("sent all messages to price server");
                                    }
                                }
                                _ = priceClient.BeginReceiveFrom(state.PriceBuffer, 0, State.BUFFER_SIZE, SocketFlags.None, ref ep, new AsyncCallback(OnReceivePrice), state);
                            }
                        }
                        catch (ObjectDisposedException)
                        {
                            Log("Price client closed already");
                        }
                        _ = client.BeginReceiveFrom(state.Buffer, 0, State.BUFFER_SIZE, SocketFlags.None, ref ep, new AsyncCallback(OnReceive), state);
                    }
                }
            }
            catch (Exception e)
            {
                Log(e.Message);
                Log(e.StackTrace);
            }
            finally
            {
                //_ = socket.BeginReceiveFrom(state.Buffer, 0, State.BUFFER_SIZE, SocketFlags.None, ref ep, new AsyncCallback(OnReceive), state);
            }
        }

        private static void OnReceivePrice(IAsyncResult result)
        {
            State state = (State)result.AsyncState;
            Socket client = state.WorkingSocket;
            Socket priceClient = state.PriceServerSocket;
            EndPoint ep = priceClient.RemoteEndPoint;
            StringBuilder sb = new();

            if (ep == null)
            {
                return;
            }

            try
            {
                int size = priceClient.EndReceiveFrom(result, ref ep);
                if (size > 0)
                {
                    try
                    {
                        if (priceClient.Connected)
                        {
                            using NetworkStream ns = new(priceClient, false);
                            using BufferedStream bs = new(ns, State.BUFFER_SIZE);
                            if (bs.CanRead)
                            {
                                using StreamReader sr = new(bs);
                                string line = sr.ReadLine();
                                if (line == null)
                                {
                                    return;
                                }
                                if (line.StartsWith("41"))
                                {
                                    _ = client.Send(line.ToByteArray().EncodeWebsocketFrame());
                                }
                                bs.Flush();
                            }
                        }
                    }
                    catch (Exception)
                    {
                        return;
                    }
                    Array.Clear(state.PriceBuffer, 0, state.PriceBuffer.Length);
                    if (priceClient.Connected)
                    {
                        _ = priceClient.BeginReceiveFrom(state.PriceBuffer, 0, State.BUFFER_SIZE, SocketFlags.None, ref ep, new AsyncCallback(OnReceivePrice), state);
                    }
                }
            }
            catch (Exception e)
            {
                Log(e.Message);
                Log(e.StackTrace);
            }
        }

        private static void StartPriceServerClient(Socket priceSocket)
        {
            try
            {
                priceSocket.Connect(PRICE_IP, PRICE_PORT);
                Log("Connected to price server.");
            }
            catch (SocketException e)
            {
                Log(e.Message);
                Log(e.StackTrace);
            }
        }

        public static void Log(string message) => Console.WriteLine($"{DateTime.Now:dd-MM-yyyy HH:mm:ss.fffff}:    {message}");
    }

    public class State
    {
        public const int BUFFER_SIZE = 65536;
        public Socket WorkingSocket { get; set; }
        public Socket PriceServerSocket { get; set; }
        public byte[] Buffer = new byte[BUFFER_SIZE];
        public byte[] PriceBuffer = new byte[BUFFER_SIZE];
    }

    public static class Utility
    {
        public static string GetClientIPAddress(this Socket client) 
            => $"{IPAddress.Parse(((IPEndPoint)client.RemoteEndPoint).Address.ToString())}:{((IPEndPoint)client.RemoteEndPoint).Port}";

        public static string GetWebSocketKey(string handshakeRequest)
        {
            Match m = Regex.Match(handshakeRequest, @"Sec-WebSocket-Key: ([\w\/+=]+)");
            return m.Success ? m.Groups[1].Value : "";
        }

        public static string GenerateAcceptKey(string webSocketKey)
        {
            string guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
            SHA1 sha1 = SHA1.Create();
            return Convert.ToBase64String(sha1.ComputeHash($"{webSocketKey}{guid}".ToByteArray()));
        }

        public static string ConvertToString(this byte[] bytes) => Encoding.Default.GetString(bytes).Replace("\r\n", "");
        public static byte[] ToByteArray(this string str) => Encoding.Default.GetBytes(str);
        public static byte[] TrimEnd(this byte[] bytes)
        {
            Array.Resize(ref bytes, Array.FindLastIndex(bytes, b => b != 0) + 1); // retain the last \0 for terminating the string
            return bytes;
        }

        public static List<byte[]> DecodeWebsocketFrame(this byte[] bytes, State state)
        {
            List<byte[]> ret = new();
            int offset = 0;
            int opCode = bytes[offset] - 0x80;
            switch (opCode)
            {
                case 8:
                    Program.Log("Disconnecting socket and price server client.");
                    state.WorkingSocket.Shutdown(SocketShutdown.Both);
                    state.WorkingSocket.Disconnect(true);
                    state.PriceServerSocket.Shutdown(SocketShutdown.Both);
                    state.PriceServerSocket.Disconnect(true);
                    _ = Program.Manual.Set();
                    return ret;
            }

            while (offset + 6 < bytes.Length)
            {
                int len = bytes[offset + 1] - 0x80;
                if (len <= 125)
                {
                    byte[] key = new byte[] { bytes[offset + 2], bytes[offset + 3], bytes[offset + 4], bytes[offset + 5] };
                    byte[] decoded = new byte[len];
                    for (int i = 0; i < len; i++)
                    {
                        int realPos = offset + 6 + i;
                        decoded[i] = (byte)(bytes[realPos] ^ key[i % 4]);
                    }
                    offset += 6 + len;
                    ret.Add(decoded);
                }
                else
                {
                    int a = bytes[offset + 2];
                    int b = bytes[offset + 3];
                    len = (a << 8) + b;

                    byte[] key = new byte[] { bytes[offset + 4], bytes[offset + 5], bytes[offset + 6], bytes[offset + 7] };
                    byte[] decoded = new byte[len];
                    for (int i = 0; i < len; i++)
                    {
                        int realPos = offset + 8 + i;
                        decoded[i] = (byte)(bytes[realPos] ^ key[i % 4]);
                    }

                    offset += 8 + len;
                    ret.Add(decoded);
                }
            }
            return ret;
        }

        public static byte[] EncodeWebsocketFrame(this byte[] message)
        {
            bool longPacket = message.Length > 125;
            List<byte> encoded = new() { 1 + 0x80 };

            if (longPacket)
            {
                encoded.Add(0x7E);
                encoded.Add((byte)(message.Length >> 8));
            }
            encoded.Add((byte)message.Length);
            encoded.AddRange(message);
            return encoded.ToArray();
        }
    }
}
