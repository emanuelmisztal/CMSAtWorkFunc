using System;
using System.Collections.Generic;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using SendGrid;
using SendGrid.Helpers.Mail;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text;
using System.Net.Http.Json;

namespace CMS_FailedBatchProcessNotification
{
    // definition of a BatchJob object, which reflects AdminPreference message definition model in Mendix
    public class BatchJob
    {
        public string PreferenceTitle { get; set; }
        public bool IsOn { get; set; }
        public DateTime LastRunDate { get; set; }
        public int BatchRunFrequency { get; set; }
        public int AproxBatchRunTime { get; set; }
    }

    public class BatchJobsWatcher
    {
        [FunctionName("BatchJobsWatcherRun")]
        public void Run([TimerTrigger("%CRON_BatchJobsWatcherRun%")] TimerInfo myTimer, ILogger log)
        {
            log.LogInformation($"C# timer trigger function executed at: {DateTime.Now}.");

            string failedBatchJobsMessageContainer = String.Empty;
            int failedJobsCount = 0;
            List<BatchJob> batchJobsList = new();

            using (HttpClient client = new())
            {
                client.BaseAddress = new Uri(Environment.GetEnvironmentVariable("REST_Base"));
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                var byteArray = Encoding.ASCII.GetBytes(Environment.GetEnvironmentVariable("REST_User") + ":" + Environment.GetEnvironmentVariable("REST_Password"));
                client.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(byteArray));

                batchJobsList = GetBatchJobsAsync(client, log).Result;
            }

            if (batchJobsList?.Count > 0)
            {
                foreach (BatchJob batchJob in batchJobsList)
                {
                    if (batchJob.IsOn)
                    {
                        // check if job has run at least once already
                        if (batchJob.LastRunDate > DateTime.MinValue)
                        {
                            // create timespan interval that equals batch job's frequency + (aproximate job run time * 2)
                            // because admin pref last run date is updated at the end of a scheduled event and every batch job is set to skip on overlap this pattern will provide optimal time coverage
                            TimeSpan timeSpan = new(batchJob.BatchRunFrequency, batchJob.AproxBatchRunTime * 2, 0);

                            // check if batch job is running late
                            if (batchJob.LastRunDate.Add(timeSpan) < DateTime.Now)
                            {
                                failedBatchJobsMessageContainer += $"<br>{batchJob.PreferenceTitle} - has been running longer than expected or didn't run at all;";
                                failedJobsCount++;
                            }
                        }
                        // in case job has not run at least once yet
                        else
                        {
                            failedBatchJobsMessageContainer += $"<br>{batchJob.PreferenceTitle} - has not run at least once yet but it is marked to be included in this report, it should be excluded by application administrator until it is confirmed to be working properly;";
                            failedJobsCount++;
                        }
                    }
                    // in case AdminPreference is turned off
                    else
                    {
                        failedBatchJobsMessageContainer += $"<br>{batchJob.PreferenceTitle} - AdminPreference is turned off, it will be included in this report as a reminder. If you wish to exclude it an application administrator needs to mark it as not a batch job;";
                        failedJobsCount++;
                    }
                }
            }
            else
            {
                failedBatchJobsMessageContainer += $"<br>REST call did not return any data. If there are no batch jobs set in the application this reporting function should be turned off in Azure. If there are batch jobs set in the application that means there is something wrong with the REST connection.";
                failedJobsCount = 1;
            }

            if (failedJobsCount > 0)
            {
                // create list of recipents - suggesting a single distribution list for ease of maintenence (straight from AD)
                List<EmailAddress> listTo = new()
                {
                    new EmailAddress("emanuel.misztal@jacobs.com", "Emanuel Misztal")
                };

                // create list of CC recipents - if you don't like someone; might be a DL as well
                List<EmailAddress> listCC = new()
                {
                    // here goes a list of your enemies
                };

                string subject = "CMS@Work - failed batch jobs report";
                string message = new($"Failed batch jobs report:<br>{failedBatchJobsMessageContainer}<br><br>Please ensure to take necessary steps.<br>From Poland, with love.");

                SendEmail(listTo, listCC, subject, message, log);
            }
            // end of TimeTrigger function, see you space cowboy
        }

        // function definition that will retrieve AdminPreference(MX)/BatchJob(C#) object list via REST call to the application's published REST service
        private static async Task<List<BatchJob>> GetBatchJobsAsync(HttpClient client, ILogger log)
        {
            List<BatchJob> batchJoblist = new();
            HttpResponseMessage response = await client.GetAsync(client.BaseAddress + "/GetBatchJobsPreferences");
            if (response.IsSuccessStatusCode)
            {
                batchJoblist = await response.Content.ReadFromJsonAsync<List<BatchJob>>();
                if (batchJoblist.Count == 0)
                    log.LogInformation($"REST call did not return any data. If there are no batch jobs set in the application this TimeTrigger should be turned off. If there are that means there is something wrong with the REST connection to the application.");
            }
            else
                log.LogInformation($"There was a problem with a REST call: {response.StatusCode} - {response.ReasonPhrase}");
            return batchJoblist;
        }

        // function definition for sending emails, no queue though
        // if there is a problem with sending emails this function will run again in a short period of time and some of the jobs that were previously stuck might have already re-run succesfully,
        // in which case there is no need to send an email about it, notify just about current issues
        private static void SendEmail(List<EmailAddress> emailsTo, List<EmailAddress> emailsCC, string subject, string emailMessage, ILogger log)
        {
            EmailAddress fromEmail = new(Environment.GetEnvironmentVariable("SenderEmail"), Environment.GetEnvironmentVariable("SenderName"));
            var client = new SendGridClient(Environment.GetEnvironmentVariable("SendGrid_API_Key"));

            var msg = new SendGridMessage()
            {
                From = fromEmail,
                Subject = subject,
                HtmlContent = emailMessage,
            };

            msg.AddTos(emailsTo);
            if (emailsCC?.Count > 0)
                msg.AddCcs(emailsCC);

            var response = client.SendEmailAsync(msg);
            log.LogInformation($"Email service response: {response.Result.StatusCode} - {response.Status}");
        }
    }
}