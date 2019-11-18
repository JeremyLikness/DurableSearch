using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using System.Collections.Generic;
using System.Net.Http;
using System.Net;
using System.Reflection;

namespace DurableSiteMap
{
    public static class Search
    {
        private const string BaseUrl = "{baseUrl}";
        static string _searchText;
        static string SearchContent
        {
            get
            {
                return _searchText ?? (_searchText = GetSearchContent());
            }
        }

        static string GetSearchContent()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resource = $"{assembly.GetName().Name}.index.html";
            using (var stream = assembly.GetManifestResourceStream(resource))
            {
                using (var reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        [FunctionName("Search")]
        public static IActionResult DoSearch(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = null)] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("Request to conduct search.");

            var url = $"{req.Scheme}://{req.Host.Value}{req.Path.Value}".Replace("Search", string.Empty);

            var contentResult = new ContentResult
            {
                Content = SearchContent.Replace(BaseUrl, url),
                ContentType = "text/html"
            };
            return contentResult;
        }

        [FunctionName(nameof(StartSearch))]
        public static async Task<IActionResult> StartSearch(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = null)] HttpRequest req,
            [DurableClient]IDurableClient client,
            ILogger log)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");

            string query = req.Query["q"];

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);
            query = query ?? data?.q;

            if (string.IsNullOrWhiteSpace(query))
            {
                return new BadRequestObjectResult("Query (q) is required.");
            }

            var id = await client.StartNewAsync(nameof(SearchWorkflow), (object)query);

            // set a workflow that watches the workflow
            var queryCheckBase = $"{req.Scheme}://{req.Host.Value}{req.Path.Value}".Replace($"api/{nameof(StartSearch)}", string.Empty);
            var checkUrl = $"{queryCheckBase}runtime/webhooks/durabletask/instances/{id}";
            await client.StartNewAsync(nameof(WatchWorkflow), (object)checkUrl);

            return new OkObjectResult(id);
        }

        [FunctionName(nameof(GetResult))]
        public static async Task<IActionResult> GetResult(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = null)] HttpRequest req,
            [DurableClient]IDurableClient client,
            ILogger logger)
        {
            string idString = req.Query["id"];

            using (StreamReader streamReader = new StreamReader(req.Body))
            {
                string requestBody = await streamReader.ReadToEndAsync();
                dynamic data = JsonConvert.DeserializeObject(requestBody);
                idString = idString ?? data?.id;
            }
            if (string.IsNullOrWhiteSpace(idString))
            {
                return new BadRequestObjectResult("id is required.");
            }
            logger.LogInformation("Request for id {id}", idString);
            var jobStatus = await client.GetStatusAsync(idString);
            if (jobStatus == null)
            {
                return new NotFoundResult();
            }
            if (jobStatus.RuntimeStatus == OrchestrationRuntimeStatus.Canceled
                || jobStatus.RuntimeStatus == OrchestrationRuntimeStatus.Failed
                || jobStatus.RuntimeStatus == OrchestrationRuntimeStatus.Terminated)
            {
                return new BadRequestObjectResult("Orchestration failed.");
            }
            if (jobStatus.RuntimeStatus == OrchestrationRuntimeStatus.Completed)
            {
                var result = jobStatus.Output.ToObject<string[]>();
                var response = new ContentResult
                {
                    Content = string.Join("<hr/>", result),
                    ContentType = "text/html"
                };
                return response;
            }
            return new StatusCodeResult((int)HttpStatusCode.Accepted);
        }

        [FunctionName(nameof(SearchWorkflow))]
        public static async Task<string[]> SearchWorkflow(
            [OrchestrationTrigger]IDurableOrchestrationContext context,
            ILogger logger)
        {
            var search = context.GetInput<string>();

            logger.LogInformation("Queuing searches...");
           
            var searches = new List<Task<DurableHttpResponse>>()
            {
                context.CallHttpAsync(HttpMethod.Get,
                new Uri($"https://google.com/search?q={search}")),
                context.CallHttpAsync(HttpMethod.Get,
                new Uri($"https://search.yahoo.com/search?p={search}")),
                context.CallHttpAsync(HttpMethod.Get,
                new Uri($"https://bing.com/search?q={search}"))
            };

            var result = await Task.WhenAll(searches);

            logger.LogInformation("Searches complete.");
            var resultString = new List<string>();
            foreach (var response in result)
            {
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    resultString.Add(response.Content);
                }
                else
                {
                    resultString.Add("<h1>No Results</h1>");
                }
            }

            return resultString.ToArray();
        }

        [FunctionName(nameof(WatchWorkflow))]
        public static async Task<HttpStatusCode> WatchWorkflow(
            [OrchestrationTrigger]IDurableOrchestrationContext context,
            ILogger logger)
        {
            var req = context.GetInput<string>();
            logger.LogInformation("Starting watcher: {url}", req);

            var result = await context.CallHttpAsync(
                HttpMethod.Get, 
                new Uri(req, UriKind.Absolute));

            logger.LogInformation("Done watching: {url}", req);
            return result.StatusCode;
        }
    }
}
