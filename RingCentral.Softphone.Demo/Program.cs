using System;
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
using NAudio.Wave;
using SIPSorcery.net.RTP;
using TinyJson;
using Newtonsoft;
using Newtonsoft.Json;
using CenterSpace.NMath.Core;
using NAudio.Dsp;

namespace RingCentral.Softphone.Demo
{
    class Program
    {
        const int PACKETS = 3;
        const int FRAMESIZE = 235;

        // https://jmfamilysetfgenerativeaicallsummarizer20230912113534.azurewebsites.net/api/v1/callsummarizer

        //static SpeechConfig config = null;
        static StringBuilder recognizedText = new StringBuilder();
        static DateTime callAnswered;
        static DateTime callEnded;

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
                   // Console.WriteLine("Receiving...\n" + data);
                    cachedMessages += data;
                }

                client.DataReceived += OnDataReceived;

                // send message
                async void SendMessage(SipMessage sipMessage)
                {
                    var message = sipMessage.ToMessage();
  //                  Console.WriteLine("Sending...\n" + message);
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
    //                        Console.WriteLine(result);
                            var answer = rtpSession.CreateAnswer(null);
                            List<byte[]> audioBuffers = new List<byte[]>();
                            List<byte[]> audioBuffers2 = new List<byte[]>();

                            callAnswered = DateTime.UtcNow;
                            //var packets = PACKETS;
                            var framesize = FRAMESIZE;

                            recognizedText.Append("Inbound Call from Customer on ");
                            recognizedText.Append(callAnswered.ToString() + " ");

                            rtpSession.OnRtpPacketReceived +=
                                (IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket) =>
                                {
                                    var audioChunk = rtpPacket.Payload;

                                    audioBuffers.Add(audioChunk);

                                    ////drop  packets
                                    //packets--;
                                    //if (packets > 0)
                                    //{
                                    //    return;
                                    //}
                                    //packets = 0;
                                    //Console.WriteLine("OnRtpPacketReceived");
                                    
                                    // do an FFT on the audioChunk byte array to determine if there is any audio in the packet
                            
                                //    Complex[] numbers = new Complex[audioChunk.Length];

                                //float[] real = new float[audioChunk.Length];
                                //    float[] imaginary = new float[audioChunk.Length];
                                //    for (int i = 0; i < audioChunk.Length; i++)
                                //    {
                                //        real[i] = (float)audioChunk[i];
                                //        numbers[i].X = real[i];
                                //     }

                                //    FastFourierTransform.FFT(true, audioChunk.Length, numbers);
                                //    //Check the FFT to determine if sound is in the audio frequency range of 200 to 4000 Hz
                                //    //if so, process the packet
                                //    for (int i = 0; i < audioChunk.Length; i++)
                                //    {
                                //        if (numbers[i].X > 200 && numbers[i].X < 4000)
                                //        {
                                           
                                //            break;
                                //        }
                                        
                                //    }

                                    //audioBuffers2.Add(audioChunk);

                                    var audioBufferLength = rtpPacket.Payload.Length;
                                   // System.Diagnostics.Debug.WriteLine($"audioBufferLength: {audioBufferLength}");

                                    if (audioBuffers.Count == framesize)
                                    {
                                        var audioBufferToSend = new byte[audioBufferLength * framesize];
                                        for (var i = 0; i < framesize; i++)
                                        {
                                            
                                            Buffer.BlockCopy(audioBuffers[i], 0, audioBufferToSend,
                                                                                               i * audioBufferLength, audioBufferLength);
                                        }

                                        audioBufferToSend = ResampleAudioStream(audioBufferToSend);
                                       
                                       // Create a byte array to store the little-endian converted audio buffer
                                       // var audioBufferToSend = new byte[audioBufferLength * framesize];
                                       // for (var i = 0; i < framesize; i++)
                                       // {
                                       //     // Convert the audio buffer from big-endian to little-endian
                                       //     Buffer.BlockCopy(audioBuffers[i], 0, audioBufferToSend,
                                       //                         i * audioBufferLength, audioBufferLength);
                                       
                                        if ( audioBufferToSend != null)
                                    RecognitionWithPushAudioStreamAsync(audioBufferToSend, audioBufferToSend.Length).GetAwaiter().GetResult();   
                                        audioBuffers.Clear();
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

                        // For DEMO Only
#if DEBUG_DEMO
                        if (sipMessage.Subject.StartsWith("BYE"))
                        {
                            callEnded = DateTime.UtcNow;

                            // create http request  to send recognized text to the server
                            var request = (HttpWebRequest)WebRequest.Create("https://jmfamilysetfgenerativeaicallsummarizer20230912113534.azurewebsites.net/api/v1/callsummarizer");
                            request.Method = "POST";
                            request.ContentType = "application/json";
                            
                            // create a JSON object to send to the server
                            var json = "{\"ConversationId\":\"111-222358\","
                                +"\"CustomerAccountNumber\": \"1234567890\","
                                + "\"ClientId\": \"001\","
                                + "\"CallDirection\": \"Inbound\","
                                + "\"ConversationStartDateTime\": \"" + callAnswered.ToString() + "\","
                                + "\"ConversationEndDateTime\": \"" + callEnded.ToString() + "\","
                                + "\"Transcript\": \"" + recognizedText.ToString() + "\"}";

                            recognizedText.Clear();

                            var size = json.Length;
                                                        
                            //send the request  to the server   
                            using (var streamWriter = new StreamWriter(request.GetRequestStream()))
                            {
                                streamWriter.Write(json);
                                streamWriter.Flush();
                                streamWriter.Close();
                            }

                            //parse the Json Response from the server 
                            var httpResponse = (HttpWebResponse)request.GetResponse();
                            using (var streamReader = new StreamReader(httpResponse.GetResponseStream()))
                            {
                                var result = streamReader.ReadToEnd();
                                Console.WriteLine( "\n\n**********************************************\n**\t\tSummarization:\t\t**\n");
                                Console.WriteLine("**********************************************\n");
                                Console.WriteLine(ParseResults(result));
                                Console.WriteLine("\n**********************************************\n");
                               
                            }
                            
                            
                        }

#endif
                    }
                }
            }).GetAwaiter().GetResult();
        }

        private static string ParseResults(string result)
        {
            var _result = result;
            //result is in JSON format  
            //parse the JSON response into a JSON object
           
            var json = JsonConvert.DeserializeObject<Conversation>(result);

            //get the value of the "summary" attribute            
            _result = String.Format("Summary:\n\t{0}\n\nSentiment:\n\t{1}\n\nTextAnalyticsDuration:\n\t{2}\n", json.Summary
                , json.Sentiment , json.TextAnalyticsDuration.ToString());

            return _result;
        }

        public static async Task RecognitionWithPushAudioStreamAsync(byte[] audioBuffer, int audioBufferLength)
        {
           var config = SpeechConfig.FromSubscription(Environment.GetEnvironmentVariable("SPEECH_SUB_KEY"), Environment.GetEnvironmentVariable("SPEECH_REGION"));

            config.SetProfanity(ProfanityOption.Masked);

            var stopRecognition = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Create a push stream
            using (var pushStream = AudioInputStream.CreatePushStream())
            {
                using (var audioInput = AudioConfig.FromStreamInput(pushStream))
                {
                   
                    // Creates a speech recognizer using audio stream input.
                    using (var recognizer = new SpeechRecognizer(config, audioInput))
                    {
                        var phraseList = PhraseListGrammar.FromRecognizer(recognizer);

                        phraseList.AddPhrase("Volvo Car Financial");
                        phraseList.AddPhrase("South East Toyota");
                        phraseList.AddPhrase("year and model");
                        phraseList.AddPhrase("Toyota 20 23 Camry SE");                      
                        
                        // Subscribes to events.
                        recognizer.Recognizing += (s, e) =>
                        {
      //                      Console.WriteLine($"RECOGNIZING: Text={e.Result.Text}");
      //                      Console.WriteLine($"Reason: {e.Result.Reason}");
                        };

                        recognizer.Recognized += (s, e) =>
                        {
                           // Console.WriteLine($"Reason: {e.Result.Reason}");

                            if (e.Result.Reason == ResultReason.RecognizedSpeech)
                            {
                                var speechResult = e.Result.Text.Replace('.', ' ');
                                Console.Write($"{speechResult}");
                                recognizedText.Append(speechResult);                                
                                //Console.WriteLine(recognizedText.ToString());
                            }
                            else if (e.Result.Reason == ResultReason.NoMatch)
                            {
                                //Console.WriteLine($"NOMATCH: Speech could not be recognized.");
                            }
                        };

                        recognizer.Canceled += (s, e) =>
                        {
                            // Console.WriteLine($"CANCELED: Reason={e.Reason}");

                            if (e.Reason == CancellationReason.Error)
                            {
     //                           Console.WriteLine($"CANCELED: ErrorCode={e.ErrorCode}");
     //                           Console.WriteLine($"CANCELED: ErrorDetails={e.ErrorDetails}");
     //                           Console.WriteLine($"CANCELED: Did you update the subscription info?");
                            }

                            stopRecognition.TrySetResult(0);
                        };

                        recognizer.SessionStarted += (s, e) =>
                        {
       //                     Console.WriteLine("\nSession started event.");

                        };

                        recognizer.SessionStopped += (s, e) =>
                        {
                           // Console.WriteLine("\nSession stopped event.");
                           // Console.WriteLine("\nStop recognition.");
                           //// stopRecognition.TrySetResult(0);
                           // Console.WriteLine("\nStop Reason: " + e);

                        };

                        // Starts continuous recognition. Uses StopContinuousRecognitionAsync() to stop recognition.
                        await recognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);

                        // open and read the wave file and push the buffers into the recognizer
                        using (BinaryAudioStreamReader reader = new BinaryAudioStreamReader(new MemoryStream(audioBuffer, 0, audioBufferLength)))
                        {
                            byte[] buffer = new byte[160 * FRAMESIZE];
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

        private static byte[] ResampleAudioStream(byte[] audioChunk)
        {
            byte[] readBytes = null;
            try
            {

                using (MemoryStream ms = new MemoryStream(audioChunk))
                {
                    using (var rs = new RawSourceWaveStream(ms, new WaveFormat(8000, 8, 1)))
                    {
                        var wavOutFormat = new WaveFormat(16000, 16, 2);
                        var speechOutFormat = new WaveFormat(16000, 16, 1);

                        using (var resampler = new MediaFoundationResampler(rs, speechOutFormat))
                        {
                            resampler.ResamplerQuality = 60;
                            using (MemoryStream wms = new MemoryStream())
                            {
                                WaveFileWriter.WriteWavFileToStream(wms, resampler);
                                
                                var reader = new BinaryReader(wms);
                                readBytes = new byte[wms.Length];
                                readBytes = reader.ReadBytes((int)wms.Length);
                               // rs.Position = 0;
                            }
                        }
                        // Need to convert the wave file to MP3 to play in the browser.

                        //using (var wavOutSampler = new MediaFoundationResampler(rs, wavOutFormat))
                        //{
                        //    DateTime date = DateTime.UtcNow;
                        //    WaveFileWriter.CreateWaveFile($"Calllog-{date.ToString("ddMMyyyy-HHmmss")}.wav", wavOutSampler);

                        //}

                    }
                }              
                
            }
            catch (Exception ex)
            {
                 Console.WriteLine($"Send Audio Stream Error: {ex.Message}");
            }            
            return readBytes;
        }

    }
}