// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNet.Http;
using Microsoft.AspNet.HttpFeature;
using Microsoft.AspNet.HttpFeature.Security;
using Microsoft.AspNet.PipelineCore.Security;

namespace Microsoft.AspNet.Owin
{
    using SendFileFunc = Func<string, long, long?, CancellationToken, Task>;
    using WebSocketAcceptAlt =
        Func
        <
            IWebSocketAcceptContext, // WebSocket Accept parameters
            Task<WebSocket>
        >;

    public class OwinEnvironment : IDictionary<string, object>
    {
        private HttpContext _context;
        private IDictionary<string, FeatureMap> _entries;

        public OwinEnvironment(HttpContext context)
        {
            if (context.GetFeature<IHttpRequestFeature>() == null)
            {
                throw new ArgumentException("Missing required IHttpRequestFeature", "context");
            }
            if (context.GetFeature<IHttpResponseFeature>() == null)
            {
                throw new ArgumentException("Missing required IHttpResponseFeature", "context");
            }

            _context = context;
            _entries = new Dictionary<string, FeatureMap>()
            {
                { OwinConstants.RequestProtocol, new FeatureMap<IHttpRequestFeature>(feature => feature.Protocol, () => string.Empty, (feature, value) => feature.Protocol = Convert.ToString(value)) },
                { OwinConstants.RequestScheme, new FeatureMap<IHttpRequestFeature>(feature => feature.Scheme, () => string.Empty, (feature, value) => feature.Scheme = Convert.ToString(value)) },
                { OwinConstants.RequestMethod, new FeatureMap<IHttpRequestFeature>(feature => feature.Method, () => string.Empty, (feature, value) => feature.Method = Convert.ToString(value)) },
                { OwinConstants.RequestPathBase, new FeatureMap<IHttpRequestFeature>(feature => feature.PathBase, () => string.Empty, (feature, value) => feature.PathBase = Convert.ToString(value)) },
                { OwinConstants.RequestPath, new FeatureMap<IHttpRequestFeature>(feature => feature.Path, () => string.Empty, (feature, value) => feature.Path = Convert.ToString(value)) },
                { OwinConstants.RequestQueryString, new FeatureMap<IHttpRequestFeature>(feature => Utilities.RemoveQuestionMark(feature.QueryString), () => string.Empty,
                    (feature, value) => feature.QueryString = Utilities.AddQuestionMark(Convert.ToString(value))) },
                { OwinConstants.RequestHeaders, new FeatureMap<IHttpRequestFeature>(feature => feature.Headers, (feature, value) => feature.Headers = (IDictionary<string, string[]>)value) },
                { OwinConstants.RequestBody, new FeatureMap<IHttpRequestFeature>(feature => feature.Body, () => Stream.Null, (feature, value) => feature.Body = (Stream)value) },

                { OwinConstants.ResponseStatusCode, new FeatureMap<IHttpResponseFeature>(feature => feature.StatusCode, () => 200, (feature, value) => feature.StatusCode = Convert.ToInt32(value)) },
                { OwinConstants.ResponseReasonPhrase, new FeatureMap<IHttpResponseFeature>(feature => feature.ReasonPhrase, (feature, value) => feature.ReasonPhrase = Convert.ToString(value)) },
                { OwinConstants.ResponseHeaders, new FeatureMap<IHttpResponseFeature>(feature => feature.Headers, (feature, value) => feature.Headers = (IDictionary<string, string[]>)value) },
                { OwinConstants.ResponseBody, new FeatureMap<IHttpResponseFeature>(feature => feature.Body, () => Stream.Null, (feature, value) => feature.Body = (Stream)value) },
                { OwinConstants.CommonKeys.OnSendingHeaders, new FeatureMap<IHttpResponseFeature>(feature => new Action<Action<object>, object>(feature.OnSendingHeaders)) },

                { OwinConstants.CommonKeys.LocalPort, new FeatureMap<IHttpConnectionFeature>(feature => feature.LocalPort.ToString(CultureInfo.InvariantCulture),
                    (feature, value) => feature.LocalPort = Convert.ToInt32(value, CultureInfo.InvariantCulture)) },
                { OwinConstants.CommonKeys.RemotePort, new FeatureMap<IHttpConnectionFeature>(feature => feature.RemotePort.ToString(CultureInfo.InvariantCulture),
                    (feature, value) => feature.RemotePort = Convert.ToInt32(value, CultureInfo.InvariantCulture)) },

                { OwinConstants.CommonKeys.LocalIpAddress, new FeatureMap<IHttpConnectionFeature>(feature => feature.LocalIpAddress.ToString(),
                    (feature, value) => feature.LocalIpAddress = IPAddress.Parse(Convert.ToString(value))) },
                { OwinConstants.CommonKeys.RemoteIpAddress, new FeatureMap<IHttpConnectionFeature>(feature => feature.RemoteIpAddress.ToString(),
                    (feature, value) => feature.RemoteIpAddress = IPAddress.Parse(Convert.ToString(value))) },

                { OwinConstants.CommonKeys.IsLocal, new FeatureMap<IHttpConnectionFeature>(feature => feature.IsLocal, (feature, value) => feature.IsLocal = Convert.ToBoolean(value)) },

                { OwinConstants.SendFiles.SendAsync, new FeatureMap<IHttpSendFileFeature>(feature => new SendFileFunc(feature.SendFileAsync)) },

                { OwinConstants.Security.User, new FeatureMap<IHttpAuthenticationFeature>(feature => feature.User,
                    ()=> null, (feature, value) => feature.User = Utilities.MakeClaimsPrincipal((IPrincipal)value),
                    () => new HttpAuthenticationFeature())
                },
            };

            // owin.CallCancelled is required but the feature may not be present.
            object ignored;
            if (context.GetFeature<IHttpRequestLifetimeFeature>() != null)
            {
                _entries[OwinConstants.CallCancelled] = new FeatureMap<IHttpRequestLifetimeFeature>(feature => feature.RequestAborted);
            }
            else if (!_context.Items.TryGetValue(OwinConstants.CallCancelled, out ignored))
            {
                _context.Items[OwinConstants.CallCancelled] = CancellationToken.None;
            }

            // owin.Version is required.
            if (!context.Items.TryGetValue(OwinConstants.OwinVersion, out ignored))
            {
                _context.Items[OwinConstants.OwinVersion] = "1.0";
            }

            if (context.Request.IsSecure)
            {
                _entries.Add(OwinConstants.CommonKeys.ClientCertificate, new FeatureMap<IHttpClientCertificateFeature>(feature => feature.ClientCertificate,
                    (feature, value) => feature.ClientCertificate = (X509Certificate)value));
                _entries.Add(OwinConstants.CommonKeys.LoadClientCertAsync, new FeatureMap<IHttpClientCertificateFeature>(
                    feature => new Func<Task>(() => feature.GetClientCertificateAsync(CancellationToken.None))));
            }

            if (context.IsWebSocketRequest)
            {
                _entries.Add(OwinConstants.WebSocket.AcceptAlt, new FeatureMap<IHttpWebSocketFeature>(feature => new WebSocketAcceptAlt(feature.AcceptAsync)));
            }

            _context.Items[typeof(HttpContext).FullName] = _context; // Store for lookup when we transition back out of OWIN
        }

        // Public in case there's a new/custom feature interface that needs to be added.
        public IDictionary<string, FeatureMap> FeatureMaps
        {
            get { return _entries; }
        }

        void IDictionary<string, object>.Add(string key, object value)
        {
            if (_entries.ContainsKey(key))
            {
                throw new InvalidOperationException("Key already present");
            }
            _context.Items.Add(key, value);
        }

        bool IDictionary<string, object>.ContainsKey(string key)
        {
            object value;
            return ((IDictionary<string, object>)this).TryGetValue(key, out value);
        }

        ICollection<string> IDictionary<string, object>.Keys
        {
            get
            {
                object value;
                return _entries.Where(pair => pair.Value.TryGet(_context, out value))
                    .Select(pair => pair.Key).Concat(_context.Items.Keys.Select(key => Convert.ToString(key))).ToList();
            }
        }

        bool IDictionary<string, object>.Remove(string key)
        {
            if (_entries.Remove(key))
            {
                return true;
            }
            return _context.Items.Remove(key);
        }

        bool IDictionary<string, object>.TryGetValue(string key, out object value)
        {
            FeatureMap entry;
            if (_entries.TryGetValue(key, out entry) && entry.TryGet(_context, out value))
            {
                return true;
            }
            return _context.Items.TryGetValue(key, out value);
        }

        ICollection<object> IDictionary<string, object>.Values
        {
            get { throw new NotImplementedException(); }
        }

        object IDictionary<string, object>.this[string key]
        {
            get
            {
                FeatureMap entry;
                object value;
                if (_entries.TryGetValue(key, out entry) && entry.TryGet(_context, out value))
                {
                    return value;
                }
                if (_context.Items.TryGetValue(key, out value))
                {
                    return value;
                }
                throw new KeyNotFoundException(key);
            }
            set
            {
                FeatureMap entry;
                if (_entries.TryGetValue(key, out entry))
                {
                    if (entry.CanSet)
                    {
                        entry.Set(_context, value);
                    }
                    else
                    {
                        _entries.Remove(key);
                        if (value != null)
                        {
                            _context.Items[key] = value;
                        }
                    }
                }
                else
                {
                    if (value == null)
                    {
                        _context.Items.Remove(key);
                    }
                    else
                    {
                        _context.Items[key] = value;
                    }
                }
            }
        }

        void ICollection<KeyValuePair<string, object>>.Add(KeyValuePair<string, object> item)
        {
            throw new NotImplementedException();
        }

        void ICollection<KeyValuePair<string, object>>.Clear()
        {
            _entries.Clear();
            _context.Items.Clear();
        }

        bool ICollection<KeyValuePair<string, object>>.Contains(KeyValuePair<string, object> item)
        {
            throw new NotImplementedException();
        }

        void ICollection<KeyValuePair<string, object>>.CopyTo(KeyValuePair<string, object>[] array, int arrayIndex)
        {
            throw new NotImplementedException();
        }

        int ICollection<KeyValuePair<string, object>>.Count
        {
            get { return _entries.Count + _context.Items.Count; }
        }

        bool ICollection<KeyValuePair<string, object>>.IsReadOnly
        {
            get { return false; }
        }

        bool ICollection<KeyValuePair<string, object>>.Remove(KeyValuePair<string, object> item)
        {
            throw new NotImplementedException();
        }

        IEnumerator<KeyValuePair<string, object>> IEnumerable<KeyValuePair<string, object>>.GetEnumerator()
        {
            foreach (var entryPair in _entries)
            {
                object value;
                if (entryPair.Value.TryGet(_context, out value))
                {
                    yield return new KeyValuePair<string, object>(entryPair.Key, value);
                }
            }
            foreach (var entryPair in _context.Items)
            {
                yield return new KeyValuePair<string, object>(Convert.ToString(entryPair.Key), entryPair.Value);
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            throw new NotImplementedException();
        }

        public class FeatureMap
        {
            public FeatureMap(Type featureInterface, Func<object, object> getter)
                : this(featureInterface, getter, defaultFactory: null)
            {
            }
            public FeatureMap(Type featureInterface, Func<object, object> getter, Func<object> defaultFactory)
                : this(featureInterface, getter, defaultFactory, setter: null)
            {
            }

            public FeatureMap(Type featureInterface, Func<object, object> getter, Action<object, object> setter)
                : this(featureInterface, getter, defaultFactory: null, setter: setter)
            {
            }

            public FeatureMap(Type featureInterface, Func<object, object> getter, Func<object> defaultFactory, Action<object, object> setter)
                : this(featureInterface, getter, defaultFactory, setter, featureFactory: null)
            {
            }

            public FeatureMap(Type featureInterface, Func<object, object> getter, Func<object> defaultFactory, Action<object, object> setter, Func<object> featureFactory)
            {
                FeatureInterface = featureInterface;
                Getter = getter;
                Setter = setter;
                DefaultFactory = defaultFactory;
                FeatureFactory = featureFactory;
            }

            private Type FeatureInterface { get; set; }
            private Func<object, object> Getter { get; set; }
            private Action<object, object> Setter { get; set; }
            private Func<object> DefaultFactory { get; set; }
            private Func<object> FeatureFactory { get; set; }

            public bool CanSet
            {
                get { return Setter != null; }
            }

            internal bool TryGet(HttpContext context, out object value)
            {
                object featureInstance = context.GetFeature(FeatureInterface);
                if (featureInstance == null)
                {
                    value = null;
                    return false;
                }
                value = Getter(featureInstance);
                if (value == null && DefaultFactory != null)
                {
                    value = DefaultFactory();
                }
                return true;
            }

            internal void Set(HttpContext context, object value)
            {
                var feature = context.GetFeature(FeatureInterface);
                if (feature == null)
                {
                    if (FeatureFactory == null)
                    {
                        throw new InvalidOperationException("Missing feature: " + FeatureInterface.FullName); // TODO: LOC
                    }
                    else
                    {
                        feature = FeatureFactory();
                        context.SetFeature(FeatureInterface, feature);
                    }
                }
                Setter(feature, value);
            }
        }

        public class FeatureMap<TFeature> : FeatureMap
        {
            public FeatureMap(Func<TFeature, object> getter)
                : base(typeof(TFeature), feature => getter((TFeature)feature))
            {
            }

            public FeatureMap(Func<TFeature, object> getter, Func<object> defaultFactory)
                : base(typeof(TFeature), feature => getter((TFeature)feature), defaultFactory)
            {
            }

            public FeatureMap(Func<TFeature, object> getter, Action<TFeature, object> setter)
                : base(typeof(TFeature), feature => getter((TFeature)feature), (feature, value) => setter((TFeature)feature, value))
            {
            }

            public FeatureMap(Func<TFeature, object> getter, Func<object> defaultFactory, Action<TFeature, object> setter)
                : base(typeof(TFeature), feature => getter((TFeature)feature), defaultFactory,(feature, value) => setter((TFeature)feature, value))
            {
            }

            public FeatureMap(Func<TFeature, object> getter, Func<object> defaultFactory, Action<TFeature, object> setter, Func<TFeature> featureFactory)
                : base(typeof(TFeature), feature => getter((TFeature)feature), defaultFactory, (feature, value) => setter((TFeature)feature, value), () => featureFactory())
            {
            }
        }
    }
}
