using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using dotenv.net;
using RingCentral.Softphone.Net;
using SIPSorcery.Net;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using TinyJson;
using System.Net.WebSockets;

namespace RingCentral.Softphone.Demo
{
    class Program
    {      
        static RestClient rc;

        // Taken from Getting Started example
        static private async Task list_extensions()
        {
            await Console.Out.WriteLineAsync("------------------------");
            var resp = await rc.Restapi().Account().Extension().List();
            foreach (var record in resp.records)
            {
                Console.Write("| Extension " + record.extensionNumber);
                Console.Write("| Name " + record.name);
                Console.Write("| Type " + record.type);
            }
            await Console.Out.WriteLineAsync("\n------------------------\n");
        }

        // Main entry point 
        static void Main(string[] args)
        {
            // Load the .env file
            DotEnv.Load();

            Task.Run(async () =>
            {
                // Use the RingCentral RestClient to connect to RingCentral Server
                rc = new RestClient(
                    Environment.GetEnvironmentVariable("RINGCENTRAL_CLIENT_ID2"),
                    Environment.GetEnvironmentVariable("RINGCENTRAL_CLIENT_SECRET2"),
                    Environment.GetEnvironmentVariable("RINGCENTRAL_SERVER_URL")
                );

                // If you have User/ext/Pwd combination
                //await rc.Authorize(
                //    Environment.GetEnvironmentVariable("RINGCENTRAL_USERNAME"),
                //    Environment.GetEnvironmentVariable("RINGCENTRAL_EXTENSION"),
                //    Environment.GetEnvironmentVariable("RINGCENTRAL_PASSWORD")
                //);

                // We have the JavaWebToken AuthZ
                await rc.Authorize(
                    Environment.GetEnvironmentVariable("RINGCENTRAL_JWT2")
                    );

                // jwt per supervisor role
                // pull sip info 

                // Just check to see all the extensions in your environment
                list_extensions().Wait();

                // Create a SIP device by API
                var sipProvision = await rc.Restapi().ClientInfo().SipProvision().Post(new CreateSipRegistrationRequest
                {
                    sipInfo = new[]
                    {
                        new SIPInfoRequest
                        {
                            transport = "WSS"
                        }
                    }                    
                });

                // See all the Device data that is returned from RingCentral
                Console.WriteLine(sipProvision.ToJson() + "\n");

                var sipInfo = sipProvision.sipInfo[0];
                Console.WriteLine( sipInfo.ToJson() + "\n" );
                
                // Create the websocket
                var sipWebSocket = new ClientWebSocket();

                // assign the sip subprotocol to the clientwebsocket
                sipWebSocket.Options.AddSubProtocol("sip");

                sipWebSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                sipWebSocket.Options.SetRequestHeader("User-Agent", "RingCentral.Softphone.Net");
                sipWebSocket.Options.SetRequestHeader("Authorization", $"Bearer {rc.token.access_token}");
                sipWebSocket.Options.SetRequestHeader("Cookie", $"rcAccessToken={rc.token.access_token}");

                // start the connection to the WebSocket
                await sipWebSocket.ConnectAsync(new Uri("wss://" + sipInfo.outboundProxy), CancellationToken.None);

                var userAgent = "RingCentral.Softphone.Net";
                var fakeDomain = $"{Guid.NewGuid().ToString()}.invalid";
                var fakeEmail = $"{Guid.NewGuid().ToString()}@{fakeDomain}";

                var sipMessage = new SipMessage($"REGISTER sip:{sipInfo.domain} SIP/2.0", new Dictionary<string, string>
                {
                    {"Call-ID", Guid.NewGuid().ToString()},
                    {"User-Agent", userAgent},
                    {"Contact", $"<sip:{fakeEmail};transport=ws>;expires=600"},
                    {"Via", $"SIP/2.0/WSS {fakeDomain};branch=z9hG4bK{Guid.NewGuid().ToString()}"},
                    {"From", $"<sip:{sipInfo.username}@{sipInfo.domain}>;tag={Guid.NewGuid().ToString()}"},
                    {"To", $"<sip:{sipInfo.username}@{sipInfo.domain}>"},
                    {"CSeq", "8082 REGISTER"},
                    {"Content-Length", "0"},
                    {"Max-Forwards", "70"},
                }, "");

                // write
                var message = sipMessage.ToMessage();
                Console.WriteLine(message + "\n");
                var bytes = Encoding.UTF8.GetBytes(message);
                sipWebSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).Wait();
                
                // read
                var cache = new byte[10240];

                // 100 trying
                var bytesRead = await sipWebSocket.ReceiveAsync(new ArraySegment<byte>(cache), CancellationToken.None);
                //var bytesRead = await networkStream.ReadAsync(cache, 0, cache.Length);
                Console.WriteLine(Encoding.UTF8.GetString(cache, 0, bytesRead.Count));

                // 401 Unauthorized
                bytesRead = await sipWebSocket.ReceiveAsync(new ArraySegment<byte>(cache), CancellationToken.None);
                var nonceMessage = SipMessage.FromMessage(Encoding.UTF8.GetString(cache, 0, bytesRead.Count));
                
                var wwwAuth = "";
                if (nonceMessage.Headers.ContainsKey("WWW-Authenticate"))
                {
                    wwwAuth = nonceMessage.Headers["WWW-Authenticate"];
                }
                else if (nonceMessage.Headers.ContainsKey("Www-Authenticate"))
                {
                    wwwAuth = nonceMessage.Headers["Www-Authenticate"];
                }

                var regex = new Regex(", nonce=\"(.+?)\"");
                var match = regex.Match(wwwAuth);
                var nonce = match.Groups[1].Value;
                var auth = Net.Utils.GenerateAuthorization(sipInfo, "REGISTER", nonce);
                sipMessage.Headers["Authorization"] = auth;
                sipMessage.Headers["CSeq"] = "8083 REGISTER";
                sipMessage.Headers["Via"] = $"SIP/2.0/WSS {fakeDomain};branch=z9hG4bK{Guid.NewGuid().ToString()}";

                // write
                message = sipMessage.ToMessage();
                Console.WriteLine(message);
                bytes = Encoding.UTF8.GetBytes(message);
                sipWebSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).Wait();

                // 100 trying
                bytesRead = await sipWebSocket.ReceiveAsync(new ArraySegment<byte>(cache), CancellationToken.None);
                Console.WriteLine(Encoding.UTF8.GetString(cache, 0, bytesRead.Count));

                // 200 OK
                bytesRead = await sipWebSocket.ReceiveAsync(new ArraySegment<byte>(cache), CancellationToken.None);
                Console.WriteLine(Encoding.UTF8.GetString(cache, 0, bytesRead.Count));

                // Inbound INVITE
                bytesRead = await sipWebSocket.ReceiveAsync(new ArraySegment<byte>(cache), CancellationToken.None);

                Console.WriteLine(Encoding.UTF8.GetString(cache, 0, bytesRead.Count));
                var inviteMessage = Encoding.UTF8.GetString(cache, 0, bytesRead.Count);
                var inviteSipMessage = SipMessage.FromMessage(inviteMessage);

                // RTP - use SipSocery to establish a RTP session
                RTPSession rtpSession = new RTPSession(false, false, false);
                MediaStreamTrack audioTrack = new MediaStreamTrack(new List<AudioFormat>
                    {new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU)});
                rtpSession.addTrack(audioTrack);
                
                var sdpDescription = SDP.ParseSDPDescription(inviteSipMessage.Body);
                if (sdpDescription == null)
                    sdpDescription = new SDP();

                var result =
                    rtpSession.SetRemoteDescription(SdpType.offer, sdpDescription );
                Console.WriteLine(result);
                var answer = rtpSession.CreateAnswer(null);
                rtpSession.OnStarted += RtpSession_OnStarted;
                rtpSession.OnRtpEvent += RtpSession_OnRtpEvent;
                rtpSession.OnRtpPacketReceived +=
                    (IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket) =>
                    {
                        Console.WriteLine("OnRtpPacketReceived");
                    };

                sipMessage =
                    new SipMessage("SIP/2.0 200 OK", new Dictionary<string, string>
                    {
                        {"Contact", $"<sip:{fakeEmail};transport=ws>"},
                        {"Content-Type", "application/sdp"},
                        {"Content-Length", answer.ToString().Length.ToString()},
                        {"User-Agent", "RingCentral.Softphone.Net"},
                        {"Via", inviteSipMessage.Headers["Via"]},
                        {"From", inviteSipMessage.Headers["From"]},
                        {"To", $"{inviteSipMessage.Headers["To"]};tag={Guid.NewGuid().ToString()}"},
                        {"CSeq", inviteSipMessage.Headers["CSeq"]},
                        {"Supported", "outbound"},
                        {"Call-Id", inviteSipMessage.Headers["Call-Id"]},
                    }, answer.ToString());

                // write
                message = sipMessage.ToMessage();
                Console.WriteLine(message);
                bytes = Encoding.UTF8.GetBytes(message);
                sipWebSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, false, CancellationToken.None).Wait();

                // ACK
                bytesRead = await sipWebSocket.ReceiveAsync(new ArraySegment<byte>(cache), CancellationToken.None);
                Console.WriteLine(Encoding.UTF8.GetString(cache, 0, bytesRead.Count));

                await Task.Delay(1000);

                // Message
                bytesRead = await sipWebSocket.ReceiveAsync(new ArraySegment<byte>(cache), CancellationToken.None);
                Console.WriteLine(Encoding.UTF8.GetString(cache, 0, bytesRead.Count));

                await Task.Delay(1000);

                // The purpose of sending a DTMF tone is if our SDP had a private IP address then the server needs to get at least
                // one RTP packet to know where to send.
                await rtpSession.SendDtmf(0, CancellationToken.None);

                // Message
                bytesRead = await sipWebSocket.ReceiveAsync(new ArraySegment<byte>(cache), CancellationToken.None);
                Console.WriteLine(Encoding.UTF8.GetString(cache, 0, bytesRead.Count) + "\n" );

                // Do not exit, wait for the incoming audio
                await Task.Delay(999999999);
            }).GetAwaiter().GetResult();
        }

        private static void RtpSession_OnRtpEvent(IPEndPoint arg1, RTPEvent arg2, RTPHeader arg3)
        {
            Console.WriteLine("\n{0}\t{1}\t{2}\n", arg1.ToJson(), arg2.ToJson(), arg3.ToJson());
        }

        private static void RtpSession_OnStarted()
        {
            Console.WriteLine("RtpSession Started...");
        }
    }
}