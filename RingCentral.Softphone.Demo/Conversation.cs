using Microsoft.CognitiveServices.Speech.Audio;
using System;
using System.Collections.Generic;
using System.Security.Principal;
using System.Text;
using Azure.AI.TextAnalytics;
using System.Collections.Generic;
using System.Text;

namespace RingCentral.Softphone.Demo
{
    public class Conversation
    {
        public Conversation()
        {
            piiEntities = new List<PiiEntity>();
        }

        public string ConversationId { get; set; }
        public string CustomerAccountNumber { get; set; }
        public string ClientId { get; set; }
        public DateTime ConversationStartDateTime { get; set; }
        public DateTime ConversationEndDateTime { get; set; }
        public string CallDirection { get; set; }
        public TimeSpan CallDuration
        {
            get
            {
                return ConversationEndDateTime - ConversationStartDateTime;
            }
        }
        public AudioInputStream InputStream { get; set; }
        public string AudioPath { get; set; }
        public TimeSpan TranscriptionDuration { get; set; }
        public string Transcript { get; set; }
        public string Summary { get; set; }
        public string Sentiment { get; set; }
        public TimeSpan TextAnalyticsDuration { get; set; }
        public callStatus CallStatus { get; set; }
        public string Message { get; set; }
        public string PIIEntitiesFound { get; set; }
        public List<PiiEntity> piiEntities { get; set; }

    }



    public enum callStatus
    {
        Success,
        Failure_Summary,
        Failure_Transcript,
        Failure_Sentiment,
        Failure_General
    }
}
