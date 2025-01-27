using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.Linq;
using System;
using System.Threading;
using Microsoft.Azure.Storage.Blob;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;

namespace FHIRBulkImport
{
    public static class ExportOrchestrator
    {
        private static string IdentifyUniquePatientReferences(JObject resource, string patreffield, HashSet<string> uniquePats)
        {
            List<string> retVal = new List<string>();
           
            if (resource.FHIRResourceType().Equals("Bundle")) 
            {
                JArray arr = (JArray)resource["entry"];
                if (arr != null)
                {
                    foreach (JToken entry in arr)
                    {
                        var r = entry["resource"];
                        if (r == null)
                        {
                            continue;
                        }
                        string id = null;
                        if (patreffield.Equals("id"))
                        {
                            id = r.FHIRResourceId();
                        }
                        else
                        {
                            if (!r[patreffield].IsNullOrEmpty())
                            {
                                string patref = (string)r[patreffield]["reference"];
                                if (patref != null && patref.StartsWith("Patient") && patref.IndexOf("/") > 0)
                                {
                                    id = patref.Split("/")[1];
                                }
                            }
                        }
                        if (!uniquePats.Contains(id))
                        {
                            uniquePats.Add(id);
                            retVal.Add(id);
                        }
                    }
                }
            }
            return string.Join(",", retVal);
        }
        private static JObject SetContextVariables(string instanceId, string ids = null, JArray include = null)
        {
            JObject o = new JObject();
            o["instanceId"] = instanceId;
            if( ids != null) o["ids"] = ids;
            if (include != null) o["include"] = include;
            return o;
        }
       
        [FunctionName("ExportOrchestrator")]
        public static async Task<string> RunOrchestrator(
            [OrchestrationTrigger] IDurableOrchestrationContext context,
            ILogger log)
        {
            JObject config = null;
            JObject retVal = new JObject();
            HashSet<string> uniquePats = new HashSet<string>();
            retVal["instanceid"] = context.InstanceId;
           
            string inputs = context.GetInput<string>();
            try
            {
                config = JObject.Parse(inputs);
            }
            catch (Newtonsoft.Json.JsonReaderException jre)
            {
                retVal["error"] = "Not a valid JSON Object from starter input";
                log.LogError("ExportOrchestrator: Not a valid JSON Object from starter input");
                return retVal.ToString();
            }
            retVal["configuration"] = config;
            string query = (string)config["query"];
            string patreffield = (string)config["patientreferencefield"];
            JArray include = (JArray)config["include"];
            if (query==null || patreffield==null)
            {
                retVal["error"] = "query and/or patientreferencefield is empty";
                return retVal.ToString();
            }
            retVal["extractstarted"] = context.CurrentUtcDateTime;
            //get a list of N work items to process in parallel
            var tasks = new List<Task<JObject>>();
            var fhirresp = await context.CallActivityAsync<FHIRResponse>("QueryFHIR", query);
            if (fhirresp.Success && !string.IsNullOrEmpty(fhirresp.Content))
            {
                var resource = JObject.Parse(fhirresp.Content);
                //For group resource loop through the member array
                if (resource.FHIRResourceType().Equals("Group"))
                {
                    JArray ga = (JArray)resource["member"];
                    if (!ga.IsNullOrEmpty())
                    {
                        int cnt = 0;
                        var bundle = ImportNDJSON.initBundle();
                        foreach (JToken t in ga)
                        {
                            string prv = (string)t["entity"]["reference"];
                            JObject o = new JObject();
                            o["resourceType"] = "GroupInternal";
                            o["entity"] = new JObject();
                            o["entity"]["reference"] = prv;
                            ImportNDJSON.addResource(bundle, o);
                            cnt++;
                            if (cnt % 50 == 0)
                            {
                                string ids = IdentifyUniquePatientReferences(bundle, "entity", uniquePats);
                                tasks.Add(context.CallSubOrchestratorAsync<JObject>("ExportOrchestrator_ProcessPatientQueryPage", SetContextVariables(context.InstanceId,ids,include)));
                                bundle = ImportNDJSON.initBundle();                           
                            }
                        }
                        if (((JArray)bundle["entry"]).Count > 0)
                        {
                            string ids= IdentifyUniquePatientReferences(bundle, "entity", uniquePats);
                            tasks.Add(context.CallSubOrchestratorAsync<JObject>("ExportOrchestrator_ProcessPatientQueryPage", SetContextVariables(context.InstanceId, ids, include)));
                        }

                    }
                } else {
                    //Page through query results fo everything else          
                    var ids = IdentifyUniquePatientReferences(resource, patreffield, uniquePats);
                    tasks.Add(context.CallSubOrchestratorAsync<JObject>("ExportOrchestrator_ProcessPatientQueryPage", SetContextVariables(context.InstanceId, ids, include)));
                    bool nextlink = !resource["link"].IsNullOrEmpty() && ((string)resource["link"].getFirstField()["relation"]).Equals("next");
                    while (nextlink)
                    {
                        string nextpage = (string)resource["link"].getFirstField()["url"];
                        fhirresp = await context.CallActivityAsync<FHIRResponse>("QueryFHIR", nextpage);
                        if (!fhirresp.Success)
                        {
                            log.LogError($"ExportOrchestrator: FHIR Server Call Failed: {fhirresp.Status} Content:{fhirresp.Content} Query:{nextpage}");
                            nextlink = false;
                        }
                        else
                        {

                            resource = JObject.Parse(fhirresp.Content);
                            ids = IdentifyUniquePatientReferences(resource, patreffield, uniquePats);
                            tasks.Add(context.CallSubOrchestratorAsync<JObject>("ExportOrchestrator_ProcessPatientQueryPage", SetContextVariables(context.InstanceId,ids, include)));
                            nextlink = !resource["link"].IsNullOrEmpty() && ((string)resource["link"].getFirstField()["relation"]).Equals("next");
                        }
                    }
                }
                await Task.WhenAll(tasks);
                uniquePats.Clear();
                var callResults = tasks
                    .Where(t => t.Status == TaskStatus.RanToCompletion)
                    .Select(t => t.Result);
                retVal["extractcompleted"] = context.CurrentUtcDateTime;
                List<string> blobNames = new List<string>();
                JObject extractresult = new JObject();
                foreach (JObject j in callResults)
                {
                    foreach (JProperty property in j.Properties())
                    {
                        if (extractresult[property.Name] != null)
                        {
                            int total = (int)extractresult[property.Name];
                            total +=(int)property.Value;
                            extractresult[property.Name] = total;
                        } else
                        {
                            extractresult[property.Name] = property.Value;
                        }
                        if (!blobNames.Contains(property.Name)) blobNames.Add(property.Name);
                    }
                }
                retVal["extractresults"] = extractresult;
                tasks.Clear();
                var splittasks = new List<Task<string>>();
                log.LogInformation("ExportOrchestrator:Splitting Data Files into defined chunks");
                //Split Up Data Files into defined Chunks
                retVal["splitstarted"] = context.CurrentUtcDateTime;
                foreach (string bn in blobNames)
                {
                        JObject jo = new JObject();
                        jo["instanceId"] = context.InstanceId;
                        jo["blobname"] = bn;
                        splittasks.Add(context.CallActivityAsync<string>("SplitFiles", jo));
                    
                }
                await Task.WhenAll(splittasks);
                retVal["splitcompleted"] = context.CurrentUtcDateTime;
                var splitResults = splittasks
                    .Where(t => t.Status == TaskStatus.RanToCompletion)
                    .Select(t => t.Result);
                JArray jarr = new JArray();
                foreach (string s in splitResults)
                {
                    jarr.Add(s);
                }
                retVal["splitresults"] = jarr;
                string rm = retVal.ToString(Newtonsoft.Json.Formatting.None);
                await context.CallActivityAsync<bool>("AppendBlob", SetContextVariables(context.InstanceId, rm));
                log.LogInformation($"ExportOrchestrator:Instance {context.InstanceId} Completed:\r\n{rm}");
                return rm;
            }
            else
            {
                var m = $"ExportOrchestrator:Failed to communicate with FHIR Server: Status {fhirresp.Status} Response {fhirresp.Content}";
                log.LogError(m);
                return m;
            }
        }
        [FunctionName("SplitFiles")]
        public static async Task<string> SplitFiles(
         [ActivityTrigger] JToken ctx,
         ILogger log)
        {

            int maxfilesizeinmb = Utils.GetIntEnvironmentVariable("FBI-MAXFILESIZEMB", "0");
            long maxfilesizeinbytes = maxfilesizeinmb * 1024000;
            string instanceid = (string)ctx["instanceId"];
            string blobname = (string)ctx["blobname"];
            int totalbytes = 0;
            int seqno = 1;
            string destfilepath = $"{blobname}-{seqno}.xndjson";
            var appendBlobSource = await StorageUtils.GetAppendBlobClient(Utils.GetEnvironmentVariable("FBI-STORAGEACCT"), $"export/{instanceid}", $"{blobname}.xndjson");
            await appendBlobSource.SealAsync();
            var props = await appendBlobSource.GetPropertiesAsync();
            if (props.Value==null)
            {
                return $"{blobname} could not determine file size.";
            }
            long curlength = props.Value.ContentLength;
            //If maxfilesizeinmb is not set or set to zero just seal the append blob and return
            //Break Apart files into maxfilesizemb
            log.LogInformation($"SplitFiles: Splitting blob {blobname} into {maxfilesizeinmb} MB Chunks...");
            var destBlobClient = StorageUtils.GetCloudBlobClient(Utils.GetEnvironmentVariable("FBI-STORAGEACCT"));
            var destContainer = destBlobClient.GetContainerReference($"export-split-{instanceid}");
            await destContainer.CreateIfNotExistsAsync();
            CloudBlockBlob destBlob = destContainer.GetBlockBlobReference(destfilepath);
            using (var stream = appendBlobSource.OpenRead())
            {
                var outstream = destBlob.OpenWrite();
                StreamWriter writer = new StreamWriter(outstream);
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    while (!reader.EndOfStream)
                    {
                        string line = reader.ReadLine();
                        if (totalbytes + line.Length > maxfilesizeinbytes)
                        {
                            writer.Flush();
                            writer.Close();
                            destBlob = null;
                            seqno++;
                            destfilepath = $"{blobname}-{seqno}.xndjson";
                            destBlob = destContainer.GetBlockBlobReference(destfilepath);
                            outstream = destBlob.OpenWrite();
                            writer = new StreamWriter(outstream);
                            totalbytes = 0;
                        }
                        line = line + "\n";
                        writer.Write(line);
                        totalbytes += line.Length;

                    }


                }
                writer.Flush();
                writer.Close();

            }
            return $"{blobname} was split into {seqno} files";
        }
        [FunctionName("AppendBlob")]
        public static async Task<bool> AppendBlob(
           [ActivityTrigger] JToken ctx,
           ILogger log)
        {
            string instanceid = (string)ctx["instanceId"];
            string rm = (string)ctx["ids"];
            var appendBlobClient = await StorageUtils.GetAppendBlobClient(Utils.GetEnvironmentVariable("FBI-STORAGEACCT"), $"export/{instanceid}", "_completed_run.xjson");
            using (MemoryStream ms = new MemoryStream(Encoding.UTF8.GetBytes(rm)))
            {
                await appendBlobClient.AppendBlockAsync(ms);
            }
            return true;
        }
        [FunctionName("QueryFHIR")]
        public static async Task<FHIRResponse> Run(
            [ActivityTrigger] string query,
            ILogger log)
        {

            var fhirresp = await FHIRUtils.CallFHIRServer(query, "", HttpMethod.Get, log);
            return fhirresp;
        }
       
        [FunctionName("ExportOrchestrator_ProcessPatientQueryPage")]
        public static async Task<JObject> ProcessPatientQueryPage([OrchestrationTrigger] IDurableOrchestrationContext context, ILogger log)
        {
           
            JObject retVal = new JObject();
            var vars = context.GetInput<JToken>();
            try
            {
                string ids = (string)vars["ids"];
                JArray include = (JArray)vars["include"];
                if (string.IsNullOrEmpty(ids)) log.LogWarning("ExportOrchestrator_ProcessPatientQueryPage: Null/Empty Check IDS is null or empty");
                if (include == null) log.LogWarning("ExportOrchestrator_ProcessPatientQueryPage: Null Check include is null");
                if (!string.IsNullOrEmpty(ids))
                {
                        if (include != null)
                        {
                            var subtasks = new List<Task<string>>();
                            foreach (JToken t in include)
                            {
                                string sq = t.ToString();
                                sq = sq.Replace("$IDS", ids);
                                subtasks.Add(context.CallActivityAsync<string>("ExportOrchestrator_GatherResources", SetContextVariables(vars["instanceId"].ToString(),sq)));
                            }
                        
                            await Task.WhenAll(subtasks);
                            var callResults = subtasks
                                .Where(t => t.Status == TaskStatus.RanToCompletion)
                                .Select(t => t.Result);
                            foreach (string s in callResults)
                            {
                                string[] sa = s.Split(":");
                                string prop = sa[0];
                                int added = int.Parse(sa[1]);
                                JToken p = retVal[prop];
                                if (p == null)
                                {
                                    retVal[prop] = added;
                                }
                                else
                                {
                                    int val = (int)p;
                                    val += added;
                                    p = val;
                                }

                            }
                        }
                }
            } catch (Exception e)
            {
                log.LogError($"ExportOrchestrator Process Patient Page Exception:{e.Message}\r\nTrace:{e.ToString()}");
            }
            
            return retVal;
        }
        [FunctionName("ExportOrchestrator_GatherResources")]
        public static async Task<string> GatherResources([ActivityTrigger] IDurableActivityContext context, ILogger log)
        {

            JToken vars = context.GetInput<JToken>();
            int total = 0;
            string query = vars["ids"].ToString();
            string instanceid = vars["instanceId"].ToString();
            var rt = query.Split("?")[0];
            var appendBlobClient = await StorageUtils.GetAppendBlobClient(Utils.GetEnvironmentVariable("FBI-STORAGEACCT"), $"export/{instanceid}", rt + ".xndjson");
            var fhirresp = await FHIRUtils.CallFHIRServer(query, "", HttpMethod.Get, log);
            if (fhirresp.Success && !string.IsNullOrEmpty(fhirresp.Content))
            {

                    var resource = JObject.Parse(fhirresp.Content);
                    total = total + await ConvertToNDJSON(resource, appendBlobClient);
                    bool nextlink = !resource["link"].IsNullOrEmpty() && ((string)resource["link"].getFirstField()["relation"]).Equals("next");
                    while (nextlink)
                    {
                        string nextpage = (string)resource["link"].getFirstField()["url"];
                        fhirresp = await FHIRUtils.CallFHIRServer(nextpage, "", HttpMethod.Get, log);
                        if (!fhirresp.Success || string.IsNullOrEmpty(fhirresp.Content))
                        {
                            log.LogError($"ExportOrchestrator: FHIR Server Call Failed: {fhirresp.Status} Content:{fhirresp.Content} Query:{nextpage}");
                           nextlink = false;
                        }
                        else
                        {
                            resource = JObject.Parse(fhirresp.Content);
                            total = total + await ConvertToNDJSON(resource, appendBlobClient);
                            nextlink = !resource["link"].IsNullOrEmpty() && ((string)resource["link"].getFirstField()["relation"]).Equals("next");
                        }
                    }
            } else
            {
                log.LogError($"ExportOrchestrator: FHIR Server Call Failed: {fhirresp.Status} Content:{fhirresp.Content} Query:{query}");
            }
            
            return $"{rt}:{total}";
        }
       
        private static async Task<int> ConvertToNDJSON(JToken bundle, Azure.Storage.Blobs.Specialized.AppendBlobClient appendBlobClient)
        {
            int cnt = 0;
            StringBuilder sb = new StringBuilder();
            if (!bundle.IsNullOrEmpty() && bundle.FHIRResourceType().Equals("Bundle"))
            {
                JArray arr = (JArray)bundle["entry"];
                if (arr != null)
                {
                    foreach(JToken tok in arr)
                    {
                        JToken res = tok["resource"];
                        sb.Append(res.ToString(Newtonsoft.Json.Formatting.None));
                        sb.Append("\n");
                        cnt++;
                    }
                }

            }
            if (sb.Length > 0)
            {
                using (MemoryStream ms = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString())))
                {
                    await appendBlobClient.AppendBlockAsync(ms);
                }
            }
            return cnt;
        }
        [FunctionName("ExportOrchestrator_HttpStart")]
        public static async Task<HttpResponseMessage> HttpStart(
            [HttpTrigger(AuthorizationLevel.Function,"post",Route = "$alt-export")] HttpRequestMessage req,
            [DurableClient] IDurableOrchestrationClient starter,
            ILogger log)
        {

            string config = await req.Content.ReadAsStringAsync();
            var state  = await runningInstances(starter, log);
            int running = state.Count();
            int maxinstances = Utils.GetIntEnvironmentVariable("FBI-MAXEXPORTS", "0");
            if (maxinstances > 0 && running >= maxinstances)
            {
                string msg = $"Unable to start export there are {running} exports the max concurrent allowed is {maxinstances}";
                StringContent sc = new StringContent("{\"error\":\"" + msg + "\"");
                return new HttpResponseMessage() { Content = sc, StatusCode = System.Net.HttpStatusCode.TooManyRequests};
            }
            // Function input comes from the request content.
            string instanceId = await starter.StartNewAsync("ExportOrchestrator",null,config);

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");

            return starter.CreateCheckStatusResponse(req, instanceId);
        }
        [FunctionName("ExportOrchestrator_InstanceAction")]
        public static async Task<HttpResponseMessage> InstanceAction(
          [HttpTrigger(AuthorizationLevel.Function, "get", Route = "$alt-export-manage/{instanceid}")] HttpRequestMessage req,
          [DurableClient] IDurableOrchestrationClient starter,string instanceid,
          ILogger log)
        {

            var parms = System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query);
            string action = parms["action"];
            await starter.TerminateAsync(instanceid, "Terminated by User");
            StringContent sc = new StringContent($"Terminated {instanceid}");
            var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
            response.Content = sc;
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            return response;
        }
        [FunctionName("ExportOrchestrator_ExportStatus")]
        public static async Task<HttpResponseMessage> ExportStatus(
           [HttpTrigger(AuthorizationLevel.Function, "get", Route = "$alt-export-status")] HttpRequestMessage req,
           [DurableClient] IDurableOrchestrationClient starter,
           ILogger log)
        {

            string config = await req.Content.ReadAsStringAsync();
            var state = await runningInstances(starter, log);
            JArray retVal = new JArray();
            foreach (DurableOrchestrationStatus status in state)
            {
                JObject o = new JObject();
                o["instanceId"] = status.InstanceId;
                o["createdDateTime"] = status.CreatedTime;
                o["status"] = status.RuntimeStatus.ToString();
                TimeSpan span = (DateTime.UtcNow - status.CreatedTime);
                o["elapsedtimeinminutes"] = span.TotalMinutes;
                o["input"] = status.Input;
                retVal.Add(o);
            }
            StringContent sc = new StringContent(retVal.ToString());
            var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
            response.Content = sc;
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return response;
        }
        [FunctionName("ExportBlobTrigger")]
        public static async Task RunBlobTrigger([BlobTrigger("export-trigger/{name}", Connection = "FBI-STORAGEACCT")] Stream myBlob, string name, [DurableClient] IDurableOrchestrationClient starter, ILogger log)
        {

            StreamReader reader = new StreamReader(myBlob);
            var text = await reader.ReadToEndAsync();
            var state = await runningInstances(starter, log);
            int running = state.Count();
            int maxinstances = Utils.GetIntEnvironmentVariable("FBI-MAXEXPORTS", "0");
            if (maxinstances > 0 && running >= maxinstances)
            {
                string msg = $"Unable to start export there are {running} exports the max concurrent allowed is {maxinstances}";
                log.LogError($"ExportBlobTrigger:{msg}");
                return;
            }
            string instanceId = await starter.StartNewAsync("ExportOrchestrator", null, text);
            var bc = StorageUtils.GetCloudBlobClient(Utils.GetEnvironmentVariable("FBI-STORAGEACCT"));
            await StorageUtils.MoveTo(bc, "export-trigger", "export-trigger-processed", name, name, log);
            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");
        }
        public static async Task<IEnumerable<DurableOrchestrationStatus>> runningInstances(IDurableOrchestrationClient client,ILogger log)
        {
            var queryFilter = new OrchestrationStatusQueryCondition
            {
                RuntimeStatus = new[]
                {
                    OrchestrationRuntimeStatus.Pending,
                    OrchestrationRuntimeStatus.Running,
                }
                
            };

            OrchestrationStatusQueryResult result = await client.ListInstancesAsync(
                queryFilter,
                CancellationToken.None);
            var retVal = new List<DurableOrchestrationStatus>();
            foreach (DurableOrchestrationStatus status in result.DurableOrchestrationState)
            {
                if (!status.InstanceId.Contains(":"))
                {
                    retVal.Add(status);
                }
            }
            return retVal;
            
        }
    }
    
}