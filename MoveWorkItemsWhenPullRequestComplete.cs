using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Net.Http;
using Flurl.Http;
using System.Linq;
using System.Web.Http;

namespace MoveWorkItemsWhenPullRequestComplete
{
    public static class MoveWorkItemsWhenPullRequestComplete
    {
        static string Organization => Environment.GetEnvironmentVariable("Organization");
        static string PersonalAccessToken => Environment.GetEnvironmentVariable("PersonalAccessToken");

        [FunctionName("MoveWorkItemsWhenPullRequestComplete")] 
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            string kanbanColumnName, kanbanColumnId;
            kanbanColumnName = req.Query[nameof(kanbanColumnName)]; //eg. Merge request
            kanbanColumnId = req.Query[nameof(kanbanColumnId)]; //eg. WEF_CB8F301F2BD24E83BDFE30BD10998325

            if (string.IsNullOrEmpty(kanbanColumnName) || string.IsNullOrEmpty(kanbanColumnId))
                return new BadRequestErrorMessageResult($"Missing {nameof(kanbanColumnName)} or {nameof(kanbanColumnId)} query paramter");

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var jObj = JsonConvert.DeserializeObject<JToken>(requestBody);

            var repositoryId = jObj.SelectToken("$.resource.repository.id").Value<string>();
            //var projectName = jObj.SelectToken("$.resource.repository.project.name").Value<string>();
            var pullRequestId = jObj.SelectToken("$.resource.pullRequestId").Value<int>();
            var pullRequestStatus = jObj.SelectToken("$.resource.status").Value<string>();
            var mergeStatus = jObj.SelectToken("$.resource.mergeStatus").Value<string>();

            if (!(mergeStatus == "succeeded" && pullRequestStatus == "completed"))
                return new BadRequestErrorMessageResult("Pull request is not merged or not completed");

            var workItemIds = await GetWorkItemIdsFromPullRequest(repositoryId, pullRequestId);
            var workItems = await Task.WhenAll(workItemIds.Select(id => GetWorkItem(id)));
            var workItemsInMergeRequestDoingColumn = workItems.Where(wi =>
                (wi.SelectToken("$.fields.['System.BoardColumn']")?.Value<string>().Equals(kanbanColumnName) ?? false)
                &&
                (wi.SelectToken("$.fields.['System.BoardColumnDone']")?.Value<string>().Equals("False") ?? false)
            );

            workItemsInMergeRequestDoingColumn.Select(wi => wi.SelectToken("id").Value<int>()).ToList()
                .ForEach(async i => await MoveWorkItemToColumnDone(i, kanbanColumnName, kanbanColumnId));

            var resultStrings = workItemsInMergeRequestDoingColumn.Select(wi => {
                var title = wi.SelectToken("fields.['System.Title']");
                var id = wi.SelectToken("id");
                return title + $" ({id})";
            });

            string responseString =  resultStrings.Any() ? 
                responseString = "Work Items Moved: " + string.Join(", ", resultStrings) :
                responseString = "No Work Items Moved!";

            log.LogInformation(responseString);
            return new OkObjectResult(responseString);
        }


        public static async Task<JObject> GetWorkItem(int workItemId)
        {
            return await GetAzureDevOpsDataAsync($"wit/workitems/{workItemId}?api-version=5.0");
        }

        public static async Task<IEnumerable<int>> GetWorkItemIdsFromPullRequest(string repositoryId, int pullRequestId)
        {
            var jObj = await GetAzureDevOpsDataAsync($"git/repositories/{repositoryId}/pullRequests/{pullRequestId}/workitems");
            var items = jObj.SelectToken("value").Value<JArray>();
            return items.Select(jt => jt.SelectToken("id").Value<int>());
        }

        public static async Task<HttpResponseMessage> MoveWorkItemToColumnDone(int workItemId, string kanbanColumnName, string kanbanColumnId)
        {
            var url = $"https://dev.azure.com/{Organization}/_apis/wit/workitems/{workItemId}?api-version=5.1";
            var bodyObj = new dynamic[]{
                new {
                    op = "test",
                    path = "/fields/System.BoardColumn",
                    value = kanbanColumnName
                },
                new {
                    op = "replace",
                    path = $"/fields/{kanbanColumnId}_Kanban.Column.Done",
                    value = "True"
                }
            };

            var response = await url
                .WithBasicAuth("", PersonalAccessToken)
                .WithHeader("Content-Type", "application/json-patch+json")
                .PatchJsonAsync(bodyObj);
            return response;
        }

        private static async Task<JObject> GetAzureDevOpsDataAsync(string path)
        {
            var url = $"https://dev.azure.com/{Organization}/_apis/{path}";
            var response = await url.WithBasicAuth("", PersonalAccessToken).GetAsync();
            var responseBody = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<JObject>(responseBody);
        }
    }
}
