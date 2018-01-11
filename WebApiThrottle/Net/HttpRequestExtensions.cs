using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.ServiceModel.Channels;
using System.Web;

namespace WebApiThrottle.Net
{
    public static class HttpRequestExtensions
    {
        /// <summary>
        /// 
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        public static string GetClientIpAddress(this HttpRequestMessage request)
        {
            // Always return all zeroes for any failure (my calling code expects it)
            string ipAddress = "0.0.0.0";

            if (request.Properties.ContainsKey("MS_HttpContext"))
            {
                ipAddress = ((HttpContextBase)request.Properties["MS_HttpContext"]).Request.UserHostAddress;
            }
            else if (request.Properties.ContainsKey(RemoteEndpointMessageProperty.Name))
            {
                ipAddress = ((RemoteEndpointMessageProperty)request.Properties[RemoteEndpointMessageProperty.Name]).Address;
            }

            if (request.Properties.ContainsKey("MS_OwinContext"))
            {
                ipAddress = ((Microsoft.Owin.OwinContext) request.Properties["MS_OwinContext"]).Request.RemoteIpAddress;
            }

            // get the X-Forward-For headers (should only really be one)
            IEnumerable<string> xForwardForList;
            if (!request.Headers.TryGetValues("X-Forwarded-For", out xForwardForList))
            {
               return ipAddress;
            }

            var xForwardedFor = xForwardForList.FirstOrDefault();

            // check that we have a value
            if (string.IsNullOrEmpty(xForwardedFor))
            {
                return ipAddress;
            }

            // Get a list of public ip addresses in the X_FORWARDED_FOR variable
            var publicForwardingIps = xForwardedFor.Split(',').Where(ip => !IpAddressUtil.IsPrivateIpAddress(ip)).ToList();

            // If we found any, return the last one, otherwise return the user host address
            return publicForwardingIps.Any() ? publicForwardingIps.Last() : ipAddress;

        }

        public static string GetClientIpAddress(this HttpRequestBase request)
        {
            string remoteAddress = request.UserHostAddress;
            string forwardedFor = request.ServerVariables["X_FORWARDED_FOR"];
            string ipAddress = "";

            if (forwardedFor == null)
            {
                ipAddress = remoteAddress;
            }
            else
            {
                ipAddress = forwardedFor;
                if (ipAddress.IndexOf(",") > 0)
                {
                    string[] ips = ipAddress.Split(',');

                    foreach (string ip in ips)
                    {
                        if (!IpAddressUtil.IsPrivateIpAddress(ip))
                        {
                            return ip;
                        }
                    }
                }
            }
            return ipAddress;
        }
    }
}