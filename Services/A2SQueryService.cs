using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace TRPServerPanel.Services
{
    public class A2SInfoResponse
    {
        public byte Protocol { get; set; }
        public string Name { get; set; } = "";
        public string Map { get; set; } = "";
        public string Folder { get; set; } = "";
        public string Game { get; set; } = "";
        public short AppId { get; set; }
        public byte Players { get; set; }
        public byte MaxPlayers { get; set; }
        public byte Bots { get; set; }
        public char ServerType { get; set; }
        public char Environment { get; set; }
        public byte Visibility { get; set; }
        public byte Vac { get; set; }
        public string Version { get; set; } = "";
        public long Ping { get; set; }
    }

    public class A2SQueryService
    {
        private const int TimeoutMs = 2500;
        private static readonly byte[] A2S_INFO_PAYLOAD = { 
            0xFF, 0xFF, 0xFF, 0xFF, 0x54, 
            0x53, 0x6F, 0x75, 0x72, 0x63, 0x65, 0x20, 0x45, 0x6E, 0x67, 0x69, 0x6E, 0x65, 0x20, 0x51, 0x75, 0x65, 0x72, 0x79, 0x00 
        };

        public async Task<A2SInfoResponse?> QueryServerInfoAsync(string ip, int port)
        {
            using var udpClient = new UdpClient();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            
            try
            {
                udpClient.Connect(ip, port);
                
                // Set Socket level timeouts as fallback
                udpClient.Client.SendTimeout = TimeoutMs;
                udpClient.Client.ReceiveTimeout = TimeoutMs;

                await udpClient.SendAsync(A2S_INFO_PAYLOAD, A2S_INFO_PAYLOAD.Length);

                // Modern .NET 10 approach for safe async cancellation
                using var cts = new CancellationTokenSource(TimeoutMs);
                try
                {
                    var result = await udpClient.ReceiveAsync(cts.Token);
                    stopwatch.Stop();
                    var buffer = result.Buffer;

                    if (buffer.Length < 5 || buffer[4] != 0x49) return null; // 'I'

                    using var ms = new MemoryStream(buffer);
                    using var reader = new BinaryReader(ms, Encoding.UTF8);

                    ms.Position = 5;
                    var response = new A2SInfoResponse();
                    response.Protocol = reader.ReadByte();
                    response.Name = ReadNullTerminatedString(reader);
                    response.Map = ReadNullTerminatedString(reader);
                    response.Folder = ReadNullTerminatedString(reader);
                    response.Game = ReadNullTerminatedString(reader);
                    response.AppId = reader.ReadInt16();
                    response.Players = reader.ReadByte();
                    response.MaxPlayers = reader.ReadByte();
                    response.Bots = reader.ReadByte();
                    response.ServerType = (char)reader.ReadByte();
                    response.Environment = (char)reader.ReadByte();
                    response.Visibility = reader.ReadByte();
                    response.Vac = reader.ReadByte();
                    response.Version = ReadNullTerminatedString(reader);
                    // v16.5: Ensure local pings show at least 1ms instead of staying at a weird 0ms dead-flatline
                    response.Ping = Math.Max(1, stopwatch.ElapsedMilliseconds);

                    return response;
                }
                catch (OperationCanceledException)
                {
                    // Task was observed and canceled by CTS - Safe from unobserved exceptions
                    return null;
                }
            }
            catch (Exception ex)
            {
                // Silent catch for network issues, but task is observed
                System.Diagnostics.Debug.WriteLine($"[A2S] Query Exception: {ex.Message}");
            }
            return null;
        }

        private string ReadNullTerminatedString(BinaryReader reader)
        {
            var sb = new StringBuilder();
            try
            {
                while (reader.BaseStream.Position < reader.BaseStream.Length)
                {
                    char c = reader.ReadChar();
                    if (c == 0) break;
                    sb.Append(c);
                }
            }
            catch (EndOfStreamException) { }
            return sb.ToString();
        }
    }
}
