using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using WebApiThrottle.Net;

namespace WebApiThrottle
{
    public class HttpModuleThrottlingHandler
    {
        private ThrottlingCore core;
        private IPolicyRepository policyRepository;
        private ThrottlePolicy policy;

        public HttpModuleThrottlingHandler()
        {
            QuotaExceededResponseCode = (HttpStatusCode)429;
            Repository = new CacheRepository();
            core = new ThrottlingCore();
        }

        public HttpModuleThrottlingHandler(ThrottlePolicy policy,
            IPolicyRepository policyRepository,
            IThrottleRepository repository,
            IIpAddressParser ipAddressParser = null)
        {
            core = new ThrottlingCore();
            core.Repository = repository;
            Repository = repository;

            if (ipAddressParser != null)
            {
                core.IpAddressParser = ipAddressParser;
            }

            QuotaExceededResponseCode = (HttpStatusCode)429;

            this.policy = policy;
            this.policyRepository = policyRepository;

            if (policyRepository != null)
            {
                policyRepository.Save(ThrottleManager.GetPolicyKey(), policy);
            }
        }

        /// <summary>
        ///  Gets or sets the throttling rate limits policy repository
        /// </summary>
        public IPolicyRepository PolicyRepository
        {
            get { return policyRepository; }
            set { policyRepository = value; }
        }

        /// <summary>
        /// Gets or sets the throttling rate limits policy
        /// </summary>
        public ThrottlePolicy Policy
        {
            get { return policy; }
            set { policy = value; }
        }

        /// <summary>
        /// Gets or sets the throttle metrics storage
        /// </summary>
        public IThrottleRepository Repository { get; set; }

        /// <summary>
        /// Gets or sets an instance of <see cref="IThrottleLogger"/> that logs traffic and blocked requests
        /// </summary>
        public IThrottleLogger Logger { get; set; }

        /// <summary>
        /// Gets or sets a value that will be used as a formatter for the QuotaExceeded response message.
        /// If none specified the default will be: 
        /// API calls quota exceeded! maximum admitted {0} per {1}
        /// </summary>
        public string QuotaExceededMessage { get; set; }

        /// <summary>
        /// Gets or sets a value that will be used as a formatter for the QuotaExceeded response message.
        /// If none specified the default will be: 
        /// API calls quota exceeded! maximum admitted {0} per {1}
        /// </summary>
        public Func<long, RateLimitPeriod, object> QuotaExceededContent { get; set; }

        /// <summary>
        /// Gets or sets the value to return as the HTTP status 
        /// code when a request is rejected because of the
        /// throttling policy. The default value is 429 (Too Many Requests).
        /// </summary>
        public HttpStatusCode QuotaExceededResponseCode { get; set; }

        public void OnEvent(HttpContextBase context)
        {
            HttpRequestBase request = context.Request;
            HttpResponseBase response = context.Response;

            // get policy from repo
            if (policyRepository != null)
            {
                policy = policyRepository.FirstOrDefault(ThrottleManager.GetPolicyKey());
            }

            if (policy == null || (!policy.IpThrottling && !policy.ClientThrottling && !policy.EndpointThrottling))
            {
                response.StatusCode = (int)HttpStatusCode.OK;
                return;
            }

            core.Repository = Repository;
            core.Policy = policy;

            var identity = SetIdentity(request);

            if (core.IsWhitelisted(identity))
            {
                response.StatusCode = (int)HttpStatusCode.OK;
                return;
            }

            if(policy.Endpoints != null && policy.Endpoints.Count > 0)
            {
                if (!core.IsEndpointMonitored(identity))
                {
                    response.StatusCode = (int)HttpStatusCode.OK;
                    return;
                }
            }

            TimeSpan timeSpan = TimeSpan.FromSeconds(1);

            // get default rates
            var defRates = core.RatesWithDefaults(Policy.Rates.ToList());
            if (Policy.StackBlockedRequests)
            {
                // all requests including the rejected ones will stack in this order: week, day, hour, min, sec
                // if a client hits the hour limit then the minutes and seconds counters will expire and will eventually get erased from cache
                defRates.Reverse();
            }

            // apply policy
            foreach (var rate in defRates)
            {
                var rateLimitPeriod = rate.Key;
                var rateLimit = rate.Value;

                timeSpan = core.GetTimeSpanFromPeriod(rateLimitPeriod);

                // apply global rules
                core.ApplyRules(identity, timeSpan, rateLimitPeriod, ref rateLimit);

                if (rateLimit > 0)
                {
                    // increment counter
                    var requestId = ComputeThrottleKey(identity, rateLimitPeriod);
                    var throttleCounter = core.ProcessRequest(timeSpan, requestId);

                    // check if key expired
                    if (throttleCounter.Timestamp + timeSpan < DateTime.UtcNow)
                    {
                        continue;
                    }

                    // check if limit is reached
                    if (throttleCounter.TotalRequests > rateLimit)
                    {
                        // log blocked request
                        if (Logger != null)
                        {
                            Logger.Log(core.ComputeLogEntry(requestId, identity, throttleCounter, rateLimitPeriod.ToString(), rateLimit, request));
                        }

                        var message = !string.IsNullOrEmpty(this.QuotaExceededMessage)
                            ? this.QuotaExceededMessage
                            : "API calls quota exceeded! maximum admitted {0} per {1}.";

                        var content = this.QuotaExceededContent != null
                            ? this.QuotaExceededContent(rateLimit, rateLimitPeriod)
                            : string.Format(message, rateLimit, rateLimitPeriod);

                        // break execution
                        response.StatusCode = (int)QuotaExceededResponseCode;
                        response.AddHeader("Retry-After", core.RetryAfterFrom(throttleCounter.Timestamp, rateLimitPeriod));
                        response.Write(content);
                        return;
                    }
                }
            }

            // no throttling required
            response.StatusCode = (int)HttpStatusCode.OK;
            return;
        }

        private HttpRequestMessage HttpRequestBaseToHttpRequestMessage(HttpRequestBase request)
        {
            var httpRequest = new HttpRequestMessage(new HttpMethod(request.HttpMethod), request.Url);

            CopyHeaders(httpRequest, request);

            if (request.Form != null)
            {
                // Avoid a request message that will try to read the request stream twice for already parsed data.
                httpRequest.Content = new FormUrlEncodedContent(GetEnumerableForm(request.Form));
            }
            else if (request.InputStream != null)
            {
                httpRequest.Content = new StreamContent(request.InputStream);
            }

            return httpRequest;
        }

        private IEnumerable<KeyValuePair<string, string>> GetEnumerableForm(NameValueCollection form)
        {
            return form.Cast<string>().Select(key => new KeyValuePair<string, string>(key, form[key]));
        }

        private void CopyHeaders(HttpRequestMessage message, HttpRequestBase request)
        {
            foreach (string headerName in request.Headers)
            {
                string[] headerValues = request.Headers.GetValues(headerName);
                if (!message.Headers.TryAddWithoutValidation(headerName, headerValues))
                {
                    message.Content.Headers.TryAddWithoutValidation(headerName, headerValues);
                }
            }
        }

        protected virtual RequestIdentity SetIdentity(HttpRequestBase request)
        {
            string cookieCav = null;

            if (request.Cookies.Count > 0 && request.Cookies["COOKIECAV"] != null)
            {
                HttpCookie cookie = request.Cookies["COOKIECAV"];
                cookieCav = cookie.Value;
            }

            return new RequestIdentity
            {
                ClientIp = core.GetClientIp(request).ToString(),
                Endpoint = request.Url.AbsolutePath,
                ClientKey = cookieCav ?? "anon"
            };
        }

        protected virtual string ComputeThrottleKey(RequestIdentity requestIdentity, RateLimitPeriod period)
        {
            return core.ComputeThrottleKey(requestIdentity, period);
        }
    }
}
