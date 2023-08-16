﻿using System;
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
using SIPSorcery.Media;
using Newtonsoft.Json;
using System.Xml.Linq;

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
                Console.WriteLine(sipInfo.ToJson() + "\n");

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
                    {"Call-Id", Guid.NewGuid().ToString()},
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
                Console.WriteLine(Encoding.UTF8.GetString(cache, 0, bytesRead.Count));

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
                Console.WriteLine(message + "\n");
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

                // Now respond with Ringing message
                var sipRingingMsg = new SipMessage($"SIP/2.0 180 Ringing", new Dictionary<string, string>
                {
                    {"Via", inviteSipMessage.Headers["Via"] },                    
                    {"From", inviteSipMessage.Headers["To"]},
                    {"Call-Id", inviteSipMessage.Headers["Call-Id"]},
                    {"CSeq", inviteSipMessage.Headers["CSeq"]},
                    {"Contact", inviteSipMessage.Headers["Contact"] },
                    {"Supported", "outbound" },
                    {"To", inviteSipMessage.Headers["From"]},
                    {"Content-Length", "0"},                    
                }, "");

                // write
                var ringing_message = sipRingingMsg.ToMessage();
                Console.WriteLine(ringing_message + "\n");
              //  var ringmsg = $"SIP/2.0 180 Ringing\r\nVia: SIP/2.0/WSS 104.245.57.165:8083;rport;branch=z9hG4bK2h1boP-aMTQlj\r\nFrom: \"WIRELESS CALLER\" <sip:+16509999999@104.245.57.165>;tag=10.13.121.68-5070-e02ad7dc192e48\r\nCall-ID: 366abc5e1920429bb3e894f60f9388c1\r\nCSeq: 316586109 INVITE\r\nContact: <sip:fba3ccc7-f58e-4f2f-8bc3-93f6ae418fe1.invalid;transport=ws>\r\nSupported: outbound\r\nTo: \"WIRELESS CALLER\" <sip:16506666666*115@50.237.72.154>;tag=d90da9de-e261-45ec-b2cb-781e484a51de\r\nContent-Length: 0";
                bytes = Encoding.UTF8.GetBytes(ringing_message);
                sipWebSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).Wait();

                // Send 200 OK
                var sipOkMsg = new SipMessage($"SIP/2.0 200 OK", new Dictionary<string, string>
                {
                    {"Via", inviteSipMessage.Headers["Via"] },
                    {"From", inviteSipMessage.Headers["To"]},
                    {"Call-Id", inviteSipMessage.Headers["Call-Id"]},
                    {"CSeq", inviteSipMessage.Headers["CSeq"]},
                    {"Contact", inviteSipMessage.Headers["Contact"] },
                    {"Supported", "outbound" },
                    {"To", inviteSipMessage.Headers["From"]},
                    {"Content-Length", "0"},
                }, "");
                message = sipOkMsg.ToMessage();
                Console.WriteLine(message + "\n");
                bytes = Encoding.UTF8.GetBytes(message);
                sipWebSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).Wait();


                // ACK trying
                bytesRead = await sipWebSocket.ReceiveAsync(new ArraySegment<byte>(cache), CancellationToken.None);
                Console.WriteLine(Encoding.UTF8.GetString(cache, 0, bytesRead.Count) + "\n");

                // Message
                //bytesRead = await sipWebSocket.ReceiveAsync(new ArraySegment<byte>(cache), CancellationToken.None);
                //Console.WriteLine(Encoding.UTF8.GetString(cache, 0, bytesRead.Count) + "\n");

                // Now send MESSAGE msg
                var doc = new System.Xml.XmlDocument();
                doc.LoadXml(inviteSipMessage.Headers["P-rc"]);
                var toNumber = inviteSipMessage.Headers["From"].Replace("<sip:", "").Replace(">", "");
                // find @
                var atIndex = toNumber.IndexOf("@");
                if (atIndex > 0)
                {
                    toNumber = "#"+ toNumber.Substring(0, atIndex) + "@sip.devtest.ringcentral.com:5060";
                }
                
                var fromNumber = inviteSipMessage.Headers["To"].Replace("<sip:", "").Replace(">", "");
                // find @
                atIndex = fromNumber.IndexOf("@");
                if (atIndex > 0)
                {
                    fromNumber = fromNumber.Substring(0, atIndex);
                }

                doc.DocumentElement.FirstChild.Attributes["To"].Value = toNumber;

                doc.DocumentElement.FirstChild.Attributes["From"].Value = fromNumber;

                // Set Cmd attribute to 17
                doc.DocumentElement.FirstChild.Attributes["Cmd"].Value = "17";

                // set Req attribute to {F27BF503-9AE3-42FD-AF3C-FF74A243B317}
                doc.DocumentElement.FirstChild.Attributes["Req"].Value = "{F27BF503-9AE3-42FD-AF3C-FF74A243B317}";

                // clear out the body
                doc.DocumentElement.ChildNodes[1].InnerText = "";
                
                // remove body attributes
                doc.DocumentElement.ChildNodes[1].Attributes.RemoveAll();

                // add Cln attribute to body
                var cln = doc.CreateAttribute("Cln");
                cln.Value = "802398776016";

                doc.DocumentElement.ChildNodes[1].Attributes.Append(cln);

                var bodyMsg = doc.OuterXml;
                var sipMessageMsg = new SipMessage($"MESSAGE sip:{sipInfo.domain} SIP/2.0", new Dictionary<string, string>
                {
                    {"From", $"<sip:{fromNumber}@sip.devtest.ringcentral.com>;tag={Guid.NewGuid().ToString()}"},
                    {"To", $"<sip:{toNumber}@sip.devtest.ringcentral.com:5600>"},                    
                    {"Content-Type", "x-rc/agent"},
                    {"CSeq", "8084 MESSAGE"},
                    {"Call-Id", inviteSipMessage.Headers["Call-Id"]},
                    {"User-Agent", userAgent},
                    {"Via", $"SIP/2.0/WSS {fakeDomain};branch=z9hG4bK{Guid.NewGuid().ToString()}"},
                    {"Content-Length", bodyMsg.Length.ToString() },
                    {"Max-Forwards", "70"},
                }, bodyMsg);

                // write
                var messageMsg = sipMessageMsg.ToMessage();
                Console.WriteLine(messageMsg + "\n");

                //var msgmsg = $"MESSAGE sip:#1336016@sip.ringcentral.com:5060 SIP/2.0\r\nFrom: <sip:16506666666*115@sip.ringcentral.com>;tag=a3e356b9-d9e5-4962-a4b0-6013941f821e\r\nTo: <sip:#1336016@sip.ringcentral.com:5060>\r\nContent-Type: x-rc/agent\r\nCSeq: 8084 MESSAGE\r\nCall-ID: 443c6592-4708-4f91-8bed-4af03304c24e\r\nUser-Agent: ringcentral-softphone-go\r\nVia: SIP/2.0/WSS fba3ccc7-f58e-4f2f-8bc3-93f6ae418fe1.invalid;branch=z9hG4bK0d18207a-96f5-4ff0-94f5-0908e48da9ad\r\nContent-Length: 179\r\nMax-Forwards: 70\r\n<Msg><Hdr SID=\"35488554330848\" Req=\"{{F27BF503-9AE3-42FD-AF3C-FF74A243B317}}\" From=\"16506666666*115\" To=\"#1336016@sip.ringcentral.com:5060\" Cmd=\"17\"/><Bdy Cln=\"802398776016\"/></Msg>";
                bytes = Encoding.UTF8.GetBytes(messageMsg);
                await sipWebSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);

               

                // 100 trying
                //bytesRead = await sipWebSocket.ReceiveAsync(new ArraySegment<byte>(cache), CancellationToken.None);
                //Console.WriteLine(Encoding.UTF8.GetString(cache, 0, bytesRead.Count) + "\n");

                // 200 OK
                //bytesRead = await sipWebSocket.ReceiveAsync(new ArraySegment<byte>(cache), CancellationToken.None);
                //Console.WriteLine(Encoding.UTF8.GetString(cache, 0, bytesRead.Count) + "\n");

                Console.WriteLine("\n--------------------\n\n\tReady for RTCPeering\n-------------------\n");

                // RTP - use SipSocery to establish a RTP session
                //"stun:" + sipInfo.stunServers[0]; //
                string STUN_URL = "stun:" + sipInfo.stunServers[0];
                //"stun:74.125.194.127:19302"; // "stun:" + sipInfo.stunServers[0]; // "stun:stun.sipsorcery.com";

                RTCConfiguration config = new RTCConfiguration
                {
                    iceServers = new List<RTCIceServer> { new RTCIceServer { urls = STUN_URL } }
                };
                var rtcPeer = new RTCPeerConnection(config);

                var audioSource = new AudioExtrasSource(new AudioEncoder(), new AudioSourceOptions { AudioSource = AudioSourcesEnum.Silence });

                //RTPSession rtpSession = new RTPSession(false, false, false);
                MediaStreamTrack audioTrack = new MediaStreamTrack(new List<AudioFormat>
                    {new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU)}, MediaStreamStatusEnum.SendRecv );
                //rtpSession.addTrack(audioTrack);
                rtcPeer.addTrack(audioTrack);

                audioSource.OnAudioSinkError += (error) => { Console.WriteLine(error); };
                audioSource.OnSendFromAudioStreamComplete += AudioSource_OnSendFromAudioStreamComplete;

                audioSource.OnAudioSourceEncodedSample += (durationRtpUnits, sample) =>
                {
                    //rtpSession.SendAudioFrame(sample);
                    rtcPeer.SendAudio(durationRtpUnits, sample);
                };  

                audioSource.OnAudioSourceError += (error) => { Console.WriteLine(error); };

                rtcPeer.onconnectionstatechange += async (state) =>
                {
                    Console.WriteLine($"Peer connection state change to {state}.\n");

                    if (state == RTCPeerConnectionState.connected)
                    {
                        await audioSource.StartAudio();
                       // await windowsVideoEndPoint.StartVideo();
                       // await testPatternSource.StartVideo();
                    }
                    else if (state == RTCPeerConnectionState.failed)
                    {
                        rtcPeer.Close("ice disconnection");
                    }
                    else if (state == RTCPeerConnectionState.closed)
                    {
                     //   await testPatternSource.CloseVideo();
                     //   await windowsVideoEndPoint.CloseVideo();
                        await audioSource.CloseAudio();
                    }
                };

                await rtcPeer.Start();

                var offerAnswer = rtcPeer.createOffer(null);
                await rtcPeer.setLocalDescription(offerAnswer);

                //var sdpDescription = SDP.ParseSDPDescription(inviteSipMessage.Body);
                //if (sdpDescription == null)
                //    sdpDescription = new SDP();

                //await Console.Out.WriteLineAsync(offerAnswer.ToJson() + "\n");


                //Console.WriteLine(result);
                //var answer = rtpSession.CreateAnswer(null);
                rtcPeer.OnStarted += RtcPeer_OnStarted;
               
                rtcPeer.OnRtpPacketReceived += RtcPeer_OnRtpPacketReceived;
                rtcPeer.OnRtcpBye += RtcPeer_OnRtcpBye;

                // write
                var sipRtpMsg = new SipMessage($"MESSAGE sip:{sipInfo.domain} SIP/2.0", new Dictionary<string, string>
                {
                    {"From", $"<sip:{fromNumber}@sip.devtest.ringcentral.com>;tag={Guid.NewGuid().ToString()}"},
                    {"To", $"<sip:{toNumber}@sip.devtest.ringcentral.com:5600>"},
                    {"Content-Type", "application/sdp"},
                    {"Call-Id", Guid.NewGuid().ToString() },
                    {"User-Agent", userAgent},
                    {"CSeq", inviteSipMessage.Headers["CSeq"]},
                    {"Contact", inviteSipMessage.Headers["Contact"] },
                    {"Via", $"SIP/2.0/WSS {fakeDomain};branch=z9hG4bK{Guid.NewGuid().ToString()}"},
                    {"Content-Length", offerAnswer.sdp.Length.ToString() },
                    {"Supported", "outbound" },
                    {"Max-Forwards", "70"},
                }, offerAnswer.sdp );

                message = sipRtpMsg.ToMessage();
                Console.WriteLine(message + "\n");
                bytes = Encoding.UTF8.GetBytes(message);
                sipWebSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, false, CancellationToken.None).Wait();

               
               // await Task.Delay(1000);

                // ICE
                rtcPeer.onicecandidate += (iceCandidate) =>
                {
                    if (rtcPeer.signalingState == RTCSignalingState.have_remote_offer)
                    {
                        //Context.WebSocket.Send(iceCandidate.toJSON());
                        Console.WriteLine(iceCandidate.ToJson() + "\n");
                        bytes = Encoding.UTF8.GetBytes(iceCandidate.ToJson());
                        sipWebSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, false, CancellationToken.None).Wait();

                        var bytesReadTask = sipWebSocket.ReceiveAsync(new ArraySegment<byte>(cache), CancellationToken.None);
                        bytesReadTask.Wait();
                        bytesRead = bytesReadTask.Result;
                        var iceResponse = Encoding.UTF8.GetString(cache, 0, bytesRead.Count);
                        Console.WriteLine(iceResponse + "\n");
                        var iceCandidateInit = JsonConvert.DeserializeObject<RTCIceCandidateInit>(iceResponse);
                        rtcPeer.addIceCandidate(iceCandidateInit);

                    }
                };

                // ACK
                bytesRead = await sipWebSocket.ReceiveAsync(new ArraySegment<byte>(cache), CancellationToken.None);
                Console.WriteLine(Encoding.UTF8.GetString(cache, 0, bytesRead.Count) + "\n");

                // await Task.Delay(1000);
                
                // Message
               // bytesRead = await sipWebSocket.ReceiveAsync(new ArraySegment<byte>(cache), CancellationToken.None);
                //Console.WriteLine(Encoding.UTF8.GetString(cache, 0, bytesRead.Count));


                // The purpose of sending a DTMF tone is if our SDP had a private IP address then the server needs to get at least
                // one RTP packet to know where to send.
                await rtcPeer.SendDtmf(0, CancellationToken.None);

                // Message
                //bytesRead = await sipWebSocket.ReceiveAsync(new ArraySegment<byte>(cache), CancellationToken.None);
                //Console.WriteLine(Encoding.UTF8.GetString(cache, 0, bytesRead.Count) + "\n");

                // Do not exit, wait for the incoming audio
                await Task.Delay(999999999);
            }).GetAwaiter().GetResult();
        }

        private static void RtcPeer_OnRtcpBye(string obj)
        {
            Console.WriteLine(  "bye\n");
        }

        private static void AudioSource_OnSendFromAudioStreamComplete()
        {
            Console.WriteLine(  "audiosource on send from stream complete\n");
        }

        
        private static void RtcPeer_OnRtpPacketReceived(IPEndPoint arg1, SDPMediaTypesEnum arg2, RTPPacket arg3)
        {
            //(IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket) =>
            //{
            Console.WriteLine("OnRtpPacketReceived");
            //};

        }

        private static void RtcPeer_OnStarted()
        {
            Console.WriteLine("RtcPeer Started...\n");
        }

      
    }
}