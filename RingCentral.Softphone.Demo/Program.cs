﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using dotenv.net;
using Nager.TcpClient;
using Org.BouncyCastle.Asn1.X509;
using RingCentral.Softphone.Net;
using SIPSorcery.Net;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using System.IO;

namespace RingCentral.Softphone.Demo
{
    class Program
    {
        //static SpeechConfig config = null;
        
        static void Main(string[] args)
        {
            DotEnv.Load(new DotEnvOptions().WithOverwriteExistingVars());
            
            Task.Run(async () =>
            {
                var sipInfo = new SipInfoResponse();
                sipInfo.domain = Environment.GetEnvironmentVariable("SIP_INFO_DOMAIN");
                sipInfo.password = Environment.GetEnvironmentVariable("SIP_INFO_PASSWORD");
                sipInfo.outboundProxy = Environment.GetEnvironmentVariable("SIP_INFO_OUTBOUND_PROXY");
                sipInfo.authorizationId = Environment.GetEnvironmentVariable("SIP_INFO_AUTHORIZATION_ID");
                sipInfo.username = Environment.GetEnvironmentVariable("SIP_INFO_USERNAME");

               // Program.config = SpeechConfig.FromSubscription(Environment.GetEnvironmentVariable("SPEECH_SUB_KEY"), Environment.GetEnvironmentVariable("SPEECH_REGION"));

                var client = new TcpClient();
                var tokens = sipInfo.outboundProxy!.Split(":");
                await client.ConnectAsync(tokens[0], int.Parse(tokens[1]));

                var userAgent = "RingCentral.Softphone.Net";
                var fakeDomain = $"{Guid.NewGuid().ToString()}.invalid";
                var fakeEmail = $"{Guid.NewGuid().ToString()}@{fakeDomain}";

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

                var cachedMessages = "";

                // receive message
                void OnDataReceived(byte[] receivedData)
                {
                    var data = Encoding.UTF8.GetString(receivedData);
                    Console.WriteLine("Receiving...\n" + data);
                    cachedMessages += data;
                }

                client.DataReceived += OnDataReceived;

                // send message
                async void SendMessage(SipMessage sipMessage)
                {
                    var message = sipMessage.ToMessage();
                    Console.WriteLine("Sending...\n" + message);
                    var bytes = Encoding.UTF8.GetBytes(message);
                    await client.SendAsync(bytes);
                }

                // send first registration message
                SendMessage(registrationMessage);

                RTPSession latestSession = null;

                // wait for server messages forever
                while (true)
                {
                    await Task.Delay(100);
                    if (cachedMessages.Length > 0)
                    {
                        // just in case, sometimes we only receive half a message, wait for the other half
                        await Task.Delay(100);

                        var tempMessages = cachedMessages.Split("\r\n\r\nSIP/2.0 ");
                        // sometimes we receive two messages in one data
                        if (tempMessages.Length > 1)
                        {
                            // in this case, we only need the second one
                            cachedMessages = "SIP/2.0 " + tempMessages[1];
                        }

                        var sipMessage = SipMessage.FromMessage(cachedMessages);

                        // reset variables
                        cachedMessages = "";

                        // the message after we reply to INVITE
                        if (sipMessage.Subject.StartsWith("ACK sip:"))
                        {
                            // The purpose of sending a DTMF tone is if our SDP had a private IP address then the server needs to get at least
                            // one RTP packet to know where to send.
                            await latestSession!.SendDtmf(0, CancellationToken.None);
                        }

                        // authorize failed with nonce in header
                        if (sipMessage.Subject.StartsWith("SIP/2.0 401 Unauthorized"))
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
                            var rtpSession = new RTPSession(false, false, false);
                            latestSession = rtpSession;
                            var inviteSipMessage = sipMessage;
                            MediaStreamTrack audioTrack = new MediaStreamTrack(new List<AudioFormat>
                                {new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU)});
                            rtpSession.addTrack(audioTrack);
                            var result =
                                rtpSession.SetRemoteDescription(SdpType.offer,
                                    SDP.ParseSDPDescription(inviteSipMessage.Body));
                            Console.WriteLine(result);
                            var answer = rtpSession.CreateAnswer(null);
                            List<byte[]> audioBuffer = new List<byte[]>();

                            var packets = 3;
                            var framesize = 2;

                            rtpSession.OnRtpPacketReceived +=
                                (IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket) =>
                                {
                                    //drop 3 packets
                                    packets--;
                                    if (packets > 0)
                                    {
                                        return;
                                    }
                                    packets = 0;
                                    //Console.WriteLine("OnRtpPacketReceived");

                                    //Let's send packets every 8000 samples (100ms) 
                                    audioBuffer.Add(rtpPacket.Payload);
                                    var audioBufferLength = rtpPacket.Payload.Length;
                                    if (audioBuffer.Count == framesize)
                                    {
                                        var audioBufferToSend = new byte[audioBufferLength * framesize];
                                        for (var i = 0; i < framesize; i++)
                                        {
                                            Buffer.BlockCopy(audioBuffer[i], 0, audioBufferToSend,
                                                                                               i * audioBufferLength, audioBufferLength);
                                        }

                                        RecognitionWithPushAudioStreamAsync(audioBufferToSend, audioBufferLength * framesize).GetAwaiter().GetResult();   
                                        audioBuffer.Clear();
                                    }

                                    
                                };

                            sipMessage =
                                new SipMessage("SIP/2.0 200 OK", new Dictionary<string, string>
                                {
                                    {"Contact", $"<sip:{fakeEmail};transport=tcp>"},
                                    {"Content-Type", "application/sdp"},
                                    {"Content-Length", answer.ToString().Length.ToString()},
                                    {"User-Agent", "RingCentral.Softphone.Net"},
                                    {"Via", inviteSipMessage.Headers["Via"]},
                                    {"From", inviteSipMessage.Headers["From"]},
                                    {"To", $"{inviteSipMessage.Headers["To"]};tag={Guid.NewGuid().ToString()}"},
                                    {"CSeq", inviteSipMessage.Headers["CSeq"]},
                                    {"Supported", "outbound"},
                                    {"Call-Id", inviteSipMessage.Headers["Call-Id"]}
                                }, answer.ToString());
                            SendMessage(sipMessage);
                        }
                    }
                }
            }).GetAwaiter().GetResult();
        }

        public static async Task RecognitionWithPushAudioStreamAsync(byte[] audioBuffer, int audioBufferLength)
        {
           var config = SpeechConfig.FromSubscription(Environment.GetEnvironmentVariable("SPEECH_SUB_KEY"), Environment.GetEnvironmentVariable("SPEECH_REGION"));

            var stopRecognition = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Create a push stream
            using (var pushStream = AudioInputStream.CreatePushStream())
            {
                using (var audioInput = AudioConfig.FromStreamInput(pushStream))
                {
                    // Creates a speech recognizer using audio stream input.
                    using (var recognizer = new SpeechRecognizer(config, audioInput))
                    {
                        // Subscribes to events.
                        recognizer.Recognizing += (s, e) =>
                        {
                            Console.WriteLine($"RECOGNIZING: Text={e.Result.Text}");
                        };

                        recognizer.Recognized += (s, e) =>
                        {
                            if (e.Result.Reason == ResultReason.RecognizedSpeech)
                            {
                                Console.WriteLine($"RECOGNIZED: Text={e.Result.Text}");
                            }
                            else if (e.Result.Reason == ResultReason.NoMatch)
                            {
                                Console.WriteLine($"NOMATCH: Speech could not be recognized.");
                            }
                        };

                        recognizer.Canceled += (s, e) =>
                        {
                            Console.WriteLine($"CANCELED: Reason={e.Reason}");

                            if (e.Reason == CancellationReason.Error)
                            {
                                Console.WriteLine($"CANCELED: ErrorCode={e.ErrorCode}");
                                Console.WriteLine($"CANCELED: ErrorDetails={e.ErrorDetails}");
                                Console.WriteLine($"CANCELED: Did you update the subscription info?");
                            }

                            stopRecognition.TrySetResult(0);
                        };

                        recognizer.SessionStarted += (s, e) =>
                        {
                            Console.WriteLine("\nSession started event.");
                        };

                        recognizer.SessionStopped += (s, e) =>
                        {
                            Console.WriteLine("\nSession stopped event.");
                            Console.WriteLine("\nStop recognition.");
                            stopRecognition.TrySetResult(0);
                        };

                        // Starts continuous recognition. Uses StopContinuousRecognitionAsync() to stop recognition.
                        await recognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);

                        // open and read the wave file and push the buffers into the recognizer
                        using (BinaryAudioStreamReader reader = new BinaryAudioStreamReader(new MemoryStream(audioBuffer, 0, audioBufferLength)))
                        {
                            byte[] buffer = new byte[1000];
                            while (true)
                            {
                                var readSamples = reader.Read(buffer, (uint)buffer.Length);
                                if (readSamples == 0)
                                {
                                    break;
                                }
                                pushStream.Write(buffer, readSamples);
                            }
                        }
                        pushStream.Close();

                        // Waits for completion.
                        // Use Task.WaitAny to keep the task rooted.
                        Task.WaitAny(new[] { stopRecognition.Task });

                        // Stops recognition.
                        await recognizer.StopContinuousRecognitionAsync().ConfigureAwait(false);
                    }
                }
            }
        }
    }
}