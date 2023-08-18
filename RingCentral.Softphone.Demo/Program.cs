using System;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using dotenv.net;
using Newtonsoft.Json;
using RingCentral.Softphone.Net;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using Websocket.Client;
using TinyJson;


namespace RingCentral.Softphone.Demo
{
    class Program
    {
        static RestClient rc;
        static RTCPeerConnection rtcPeer;
        static WebsocketClient ws;
        static String sipInfoDomain;
        static string sipInfoVia;
        static string sipInfoFrom;
        static string sipInfoCSeq;
        static string sipInfoTo;
        static string sipInfoCallId;

        static private async Task list_extensions()
        {
            await Console.Out.WriteLineAsync("------------------------");
            var resp = await rc.Restapi().Account().Extension().List();
            foreach (var record in resp.records)
            {
                Console.Write("| Extension " + record.extensionNumber);
                Console.Write("| Name " + record.name);
                Console.Write("| BusinessPhone " + record.contact.businessPhone);
                Console.Write("| MobilePhone " + record.contact.mobilePhone);
                Console.Write("| Type " + record.type);
                await Console.Out.WriteLineAsync("");
            }
            await Console.Out.WriteLineAsync("\n------------------------\n");
        }

        static void Main(string[] args)
        {
            DotEnv.Load(new DotEnvOptions().WithOverwriteExistingVars());

            Task.Run(async () =>
            {
                rc = new RestClient(
                    Environment.GetEnvironmentVariable("RINGCENTRAL_CLIENT_ID"),
                    Environment.GetEnvironmentVariable("RINGCENTRAL_CLIENT_SECRET"),
                    Environment.GetEnvironmentVariable("RINGCENTRAL_SERVER_URL")
                );
                await rc.Authorize(
                    Environment.GetEnvironmentVariable("RINGCENTRAL_JWT")
                );

                await list_extensions();

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
                var sipInfo = sipProvision.sipInfo[0];
                var wsUri = "wss://" + sipInfo.outboundProxy;
                var factory = new Func<ClientWebSocket>(() =>
                {
                    var cws = new ClientWebSocket();
                    cws.Options.AddSubProtocol("sip");
                    return cws;
                });

                ws = new WebsocketClient(new Uri(wsUri), factory);
                ws.ReconnectTimeout = null;

                var userAgent = "RingCentral.Softphone.Net";
                var fakeDomain = $"{Guid.NewGuid().ToString()}.invalid";
                var fakeEmail = $"{Guid.NewGuid().ToString()}@{fakeDomain}";
                sipInfoDomain = sipInfo.domain;
                
                var registrationMessage = new SipMessage($"REGISTER sip:{sipInfo.domain} SIP/2.0",
                    new Dictionary<string, string>
                    {
                        {"Call-ID", Guid.NewGuid().ToString()},
                        {"User-Agent", userAgent},
                        {"Contact", $"<sip:{fakeEmail};transport=tcp>;expires=600"},
                        {"Via", $"SIP/2.0/TCP {fakeDomain};branch=z9hG4bK{Guid.NewGuid().ToString()}"},
                        {"From", $"<sip:{sipInfo.username}@{sipInfo.domain}>;tag={Guid.NewGuid().ToString()}"},
                        {"To", $"<sip:{sipInfo.username}@{sipInfo.domain}>"},
                        {"CSeq", "8082 REGISTER"},
                        {"Content-Length", "0"},
                        {"Max-Forwards", "70"}
                    }, "");

                ws.MessageReceived.Subscribe(responseMessage =>
                {
                    Console.WriteLine("Receiving...\n" + responseMessage.Text);
                    try
                    { 
                        var sipMessage = SipMessage.FromMessage(responseMessage.Text);
                    
                        // authorize failed with nonce in header
                        if (sipMessage.Subject == "SIP/2.0 401 Unauthorized")
                        {
                            var nonceMessage = sipMessage;
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
                            registrationMessage.Headers["Authorization"] = auth;
                            registrationMessage.Headers["CSeq"] = "8083 REGISTER";
                            registrationMessage.Headers["Via"] =
                                $"SIP/2.0/TCP {fakeDomain};branch=z9hG4bK{Guid.NewGuid().ToString()}";
                            SendMessage(registrationMessage);
                        }

                        // whenever there is an inbound call
                        if (sipMessage.Subject.StartsWith("INVITE sip:"))
                        {
                            var inviteSipMessage = sipMessage;
                            sipInfoTo = inviteSipMessage.Headers["To"] + $";tag ={Guid.NewGuid().ToString()}";

                            sipInfoFrom = inviteSipMessage.Headers["From"];
                            sipInfoVia = inviteSipMessage.Headers["Via"];
                            sipInfoCSeq = inviteSipMessage.Headers["CSeq"];
                            sipInfoCallId = inviteSipMessage.Headers["Call-Id"];

                            var audioTrack = new MediaStreamTrack(new List<AudioFormat>
                            {new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU)});

                            rtcPeer = new RTCPeerConnection(new RTCConfiguration
                            {
                                iceServers = new List<RTCIceServer> { new RTCIceServer {
                                //urls = "stun:stun.1.google.com:19302"
                                urls = "stun:74.125.194.127:19302"
                                } }
                            });

                            
                            rtcPeer.OnStarted += RtcPeer_OnStarted;
                            rtcPeer.OnRtcpBye += RtcPeer_OnRtcpBye;
                            rtcPeer.OnRtpPacketReceived += RtcPeer_OnRtpPacketReceived;
                            rtcPeer.onicecandidate += RtcPeer_onicecandidate;
                            rtcPeer.ondatachannel += RtcPeer_ondatachannel;
                           
                            rtcPeer.OnReceiveReport += (re, media, rr) => Console.WriteLine($"RTCP Receive for {media} from {re}\n");
                            rtcPeer.OnSendReport += (media, sr) => Console.WriteLine($"RTCP Send for {media}\n");


                            var audioSource = new AudioExtrasSource(new AudioEncoder(), new AudioSourceOptions { AudioSource = AudioSourcesEnum.Silence });

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

                            rtcPeer.AcceptRtpFromAny = true;

                            rtcPeer.GetRtpChannel().OnStunMessageReceived += (msg, ep, isRelay) =>
                            {
                                Console.WriteLine($"STUN message:\n{msg}\treceived from {ep}.\n");
                            };

                            rtcPeer.addTrack(audioTrack);

                            var result = rtcPeer.setRemoteDescription(new RTCSessionDescriptionInit
                            {
                                sdp = inviteSipMessage.Body,
                                type = RTCSdpType.offer
                            });
                            var answer = rtcPeer.createAnswer();
                            rtcPeer.setLocalDescription(answer);
                            rtcPeer.Start();

                            Console.WriteLine("Set Remote Description:\n" + result.ToString() + "\n");

                            sipMessage =
                                new SipMessage("SIP/2.0 200 OK", new Dictionary<string, string>
                                {
                                {"Contact", $"<sip:{fakeEmail};transport=tcp>"},
                                {"Content-Type", "application/sdp"},
                                {"Content-Length", answer.sdp.Length.ToString()},
                                {"User-Agent", "RingCentral.Softphone.Net"},
                                {"Via", inviteSipMessage.Headers["Via"]},
                                {"From", inviteSipMessage.Headers["From"]},
                                {"To", $"{inviteSipMessage.Headers["To"]};tag={Guid.NewGuid().ToString()}"},
                                {"CSeq", inviteSipMessage.Headers["CSeq"]},
                                {"Supported", "outbound"},
                                {"Call-Id", inviteSipMessage.Headers["Call-Id"]}
                                }, answer.sdp);
                            SendMessage(sipMessage);
                        }
                        
                        if (sipMessage.Subject =="")
                        {
                            
                            // maybe ice message?
                            var iceResponse = responseMessage.Text;
                            Console.WriteLine("IceResponse: " + iceResponse + "\n");
                            try
                            {
                                var iceCandidateInit = JsonConvert.DeserializeObject<RTCIceCandidateInit>(iceResponse);
                                rtcPeer.addIceCandidate(iceCandidateInit);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Response expected is Json iceCandidate message.\nMessage received was {ex}\n");
                            }

                        }
                    }
                    catch(Exception ex)
                    {
                        var iceResponse = responseMessage.Text;
                        Console.WriteLine("IceResponse: " + iceResponse + "\n");
                        //   var iceCandidateInit = JsonConvert.DeserializeObject<RTCIceCandidateInit>(iceResponse);
                        //   rtcPeer.addIceCandidate(iceCandidateInit);

                        Console.WriteLine(ex);
                    }
                }
                
                );
                await ws.Start();

                SendMessage(registrationMessage);

                await Task.Delay(999999999);
            }).GetAwaiter().GetResult();
        }

        private static void RtcPeer_ondatachannel(RTCDataChannel obj)
        {
            
        }

        private static void SendMessage(SipMessage sipMessage)
        {
            var message = sipMessage.ToMessage();
            Console.WriteLine("Sending...\n" + message);
            ws.Send(message);
        }

        private static void RtcPeer_onicecandidate(RTCIceCandidate iceCandidate)
        {
            if (rtcPeer.signalingState == RTCSignalingState.have_remote_offer)
            {
                var fakeDomain = $"{Guid.NewGuid().ToString()}.invalid";
                var fakeEmail = $"{Guid.NewGuid().ToString()}@{fakeDomain}";

                Console.WriteLine(iceCandidate.ToJson() + "\n");
                var iceMsg = new SipMessage("SIP/2.0 200 OK", new Dictionary<string, string>
                        {
                                        {"Contact", $"<sip:{fakeEmail};transport=wss>"},
                                        {"Content-Type", "application/json"},
                                        {"Content-Length", iceCandidate.ToJson().Length.ToString()},
                                        {"User-Agent", "RingCentral.Softphone.Net"},
                                        {"Via", sipInfoVia},
                                        {"From", sipInfoTo},
                                        {"To", sipInfoFrom},
                                        {"CSeq", sipInfoCSeq},
                                        {"Supported", "outbound"},
                                        {"Call-Id", sipInfoCallId}
                        }, iceCandidate.ToJson());
                SendMessage(iceMsg);

            }

        }

        private static void RtcPeer_OnRtpPacketReceived(IPEndPoint arg1, SDPMediaTypesEnum arg2, RTPPacket arg3)
        {
            Console.WriteLine("OnRtpPacketReceived");
        }

        private static void RtcPeer_OnRtcpBye(string obj)
        {
            
        }

        private static void RtcPeer_OnStarted()
        {
            Console.WriteLine("RtcPeer Connection Started\n");
        }

        private static void AudioSource_OnSendFromAudioStreamComplete()
        {
         
        }
    }
}
