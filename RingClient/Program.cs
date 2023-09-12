using System;
using System.Threading.Tasks;
using RingCentral;
using Newtonsoft.Json;
using RingCentral.Net.WebSocket;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using dotenv.net;
using System.Runtime;

namespace Supervision_Demo
{
    class MainClass
    {
        static RestClient restClient;
        static WebSocketExtension webSocketExtension;

        //static string RC_SERVER_URL = "https://platform.ringcentral.com";

        //static string RC_CLIENT_ID = "";
        //static string RC_CLIENT_SECRET = "";

        //static string RC_JWT = "";

        static string SUPERVISOR_PHONE_NAME = "Supervisor Existing Phone";

        static string SUPERVISOR_GROUP_NAME = "";
        static string supervisorDeviceId = "";
        static List<Agent> monitoredAgents = new List<Agent>();
        static string supervisorExtensionId = "";
        static bool superviseSession = false;
        public class Agent
        {
            public string id { get; set; }
            public string status { get; set; }
        }
        public static async Task Main(string[] args)
        {
            DotEnv.Load(new DotEnvOptions().WithOverwriteExistingVars());

            var RC_CLIENT_ID = Environment.GetEnvironmentVariable("PROD1_RINGCENTRAL_CLIENT_ID");
            var RC_CLIENT_SECRET = Environment.GetEnvironmentVariable("PROD1_RINGCENTRAL_CLIENT_SECRET");
            var RC_SERVER_URL = Environment.GetEnvironmentVariable("RINGCENTRAL_SERVER_URL");
            var RC_JWT = Environment.GetEnvironmentVariable("PROD1_RINGCENTRAL_JWT");
            var SUPERVISOR_GROUP_NAME = Environment.GetEnvironmentVariable("PROD1_SUPERVISOR_GROUP_NAME");
            Supervision_Demo.MainClass.SUPERVISOR_GROUP_NAME = SUPERVISOR_GROUP_NAME;

          //  Console.WriteLine("Start running!");
            try
            {
                // Instantiate the SDK
                restClient = new RestClient(RC_CLIENT_ID, RC_CLIENT_SECRET, RC_SERVER_URL);

                // Authenticate a user using a personal JWT token
                await restClient.Authorize(RC_JWT);

                webSocketExtension = new WebSocketExtension();
                await restClient.InstallExtension(webSocketExtension);

                // Get extension devices and detect a device id of the supervised device
                await get_extension_devices();
                Console.WriteLine(supervisorDeviceId);

                // Read call monitoring groups to get the expected supervisor group and grab all the monitored agents' id
                await readCallMonitoringGroup();

                // Subscribe for monitored agents telephony session event notification
                await StartWebSocketNotification();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        static async Task readCallMonitoringGroup()
        {
            try
            {
                var resp = await restClient.Restapi().Account().CallMonitoringGroups().Get();
                foreach (var group in resp.records)
                {
                    if (group.name == SUPERVISOR_GROUP_NAME)
                    {
                        var resp1 = await restClient.Restapi().Account().CallMonitoringGroups(group.id).Members().Get();
                        foreach (var member in resp1.records)
                        {
                            if (member.permissions[0] == "Monitored")
                            {
                                Console.WriteLine("Monitored Agent: " + member.extensionNumber);
                                var agent = new Agent();
                                agent.id = member.id;
                                agent.status = "Idle";
                                monitoredAgents.Add(agent);
                            }

                            else if (member.permissions[0] == "Monitoring")
                            {
                                Console.WriteLine("Supervisor: " + member.extensionNumber);
                                supervisorExtensionId = member.id;
                            }
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        static async Task StartWebSocketNotification()
        {
         //   Console.WriteLine("StartWebSocketNotification");
            try
            {
                string[] eventFilters = new string[monitoredAgents.Count];
                int i = 0;
                foreach (var agent in monitoredAgents)
                {
                    eventFilters[i++] = "/restapi/v1.0/account/~/extension/" + agent.id + "/telephony/sessions";
                }

                var subscription = await webSocketExtension.Subscribe(eventFilters, async message =>
                {
                    // do something with message
                    await parseNotificationEvent(message);

                });
           //     Console.WriteLine("Created");
            //    Console.WriteLine(subscription.SubscriptionInfo.id);
             //   Console.WriteLine("Subscribed");

                while (true)
                {
              //      Console.WriteLine("looping ...");
                    await Task.Delay(20000);
                    //await readSubscription();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        static async Task parseNotificationEvent(string message)
        {
            dynamic jsonObj = JsonConvert.DeserializeObject(message);
            var party = jsonObj.body.parties[0];
            if (party.extensionId != null)
            {
                var agent = monitoredAgents.Find(item => item.id.ToString() == party.extensionId.ToString());
                if (agent != null)
                {
                    if (party.direction == "Inbound")
                    {
                        if (party.status.code == "Proceeding")
                        {
                            agent.status = "Ringing";
                //            Console.WriteLine("Ringing");
                        }
                        else if (party.status.code == "Answered")
                        {
                //            Console.WriteLine("Answered");
                            agent.status = "Connected";
                         //   Console.WriteLine(jsonObj.body.telephonySessionId.ToString());
                            if (superviseSession == false)
                            {
                                await getCallSessionInfo(jsonObj.body.telephonySessionId.ToString(), agent.id);
                            }
                            else
                            {
                                if (party.extensionId == agent.id)
                                {
                                    await submitSessionSuperviseRequest(jsonObj.body.telephonySessionId.ToString(), agent.id);
                                }
                            }
                        }
                        else if (party.status.code == "Disconnected")
                        {
                            agent.status = "Idle";
                  //          Console.WriteLine("Idle");
                        }
                        else if (party.status.code == "Gone")
                        {
                 //           Console.WriteLine("Transfer Gone");
                        }
                    }
                }
                else
                {
                 //   Console.WriteLine("Not a monitored extension: " + party.extensionId.ToString());
                }
            }
        }

        static async Task submitSessionSuperviseRequest(string telSessionId, string extensionId)
        {
           // Console.WriteLine("Request supervise call session.");
            if (supervisorDeviceId != "")
            {
                try
                {
                    var bodyParams = new SuperviseCallSessionRequest()
                    {
                        agentExtensionId = extensionId,
                        mode = "Listen",
                        supervisorDeviceId = supervisorDeviceId
                    };

                    var resp = await restClient.Restapi().Account().Telephony().Sessions(telSessionId).Supervise().Post(bodyParams);
             //       Console.WriteLine("POST supervise succeeded");
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                }
            }
            else
            {
             //   Console.WriteLine("No supervisor's device Id");
            }
        }

        static private async Task getCallSessionInfo(string telSessionId, string agentId)
        {
           // Console.WriteLine("getCallSessionInfo");
            CallSessionObject jsonObj = await restClient.Restapi().Account().Telephony().Sessions(telSessionId).Get();
            foreach (var party in jsonObj.parties)
            {
                Console.WriteLine(JsonConvert.SerializeObject(party));
                if (party.status.code == "Disconnected")
                {
             //       Console.WriteLine("This party got disconnected => Cannot supervised" + party.id);
                }
                else if (party.status.code == "Answered")
                {
                    await submitPartySuperviseRequest(jsonObj.id, party.id, agentId);
                    await Task.Delay(3000);
                }
            }
        }

        static async Task submitPartySuperviseRequest(string telSessionId, string partyId, string extensionId)
        {
           // Console.WriteLine("Request supervise call parties.");
           // Console.WriteLine("Party id: " + partyId);
            if (supervisorDeviceId != "")
            {
                try
                {
                    var bodyParams = new PartySuperviseRequest()
                    {
                        agentExtensionId = extensionId,
                        mode = "Listen",
                        supervisorDeviceId = supervisorDeviceId
                    };

                    var resp = await restClient.Restapi().Account().Telephony().Sessions(telSessionId).Parties(partyId).Supervise().Post(bodyParams);
             //       Console.WriteLine("POST supervise succeeded");
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                }
            }
            else
            {
               // Console.WriteLine("No supervisor's device Id");
            }
        }

        static private async Task readSubscription()
        {
            try
            {
                var resp = await restClient.Restapi().Subscription().List();
               // Console.WriteLine("==========");
               // Console.WriteLine(JsonConvert.SerializeObject(resp));
               // Console.WriteLine("==========");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }
        static async Task get_extension_devices()
        {
            try
            {
                var resp = await restClient.Restapi().Account().Extension().Device().Get();
                foreach (var record in resp.records)
                {
                    //Console.WriteLine("===========");
                    //var recordStr = JsonConvert.SerializeObject(record);
                    //dynamic recordObj = JsonConvert.DeserializeObject(recordStr);
                    //Console.WriteLine(recordObj);
                    if (record.name == SUPERVISOR_PHONE_NAME)
                    {
                        supervisorDeviceId = record.id;
                 //       Console.WriteLine("Device status:", record.status);
                        await get_device_sip_info(record.id);
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }
        static async Task get_device_sip_info(string id)
        {
            try
            {
                var resp = await restClient.Restapi().Account().Device(id).Get();
                var respStr = JsonConvert.SerializeObject(resp);
                dynamic respObj = JsonConvert.DeserializeObject(respStr);
               // Console.WriteLine(respObj);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }
    }
}

/*

//TODO: send to the Summary API - need conversationId, customer account number, client id, call direction (Inbound,outbound)
//TODO: listen for Alvaria events - Login, Logout, StartCall, EndCall and Update
//TODO: When Login event occurrs start the supervisor listening code
//TODO: when the StartCallevent occurs start submit the RingCental request to get a phone call on supervisor softphone
    then record the audio stream and get transcription 
//TODO: When the EndCall event occurs stop the recording and transcription and send to Summary API
//TODO: When the Logout event occurs stop the supervisor listening code
//TODO: when Update occurs do nothing (for the time being)
//TODO: Write unit tests

*/