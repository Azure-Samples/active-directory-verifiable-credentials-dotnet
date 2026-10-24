using System;
using Microsoft.Extensions.Caching.Memory;

namespace ExternalIDVerifiedID.Models {
    public sealed class IdentityVerificationState {
        private readonly IMemoryCache _cache;

        public IdentityVerificationState(IMemoryCache cache) {
            _cache = cache;
        }

        public sealed class Transaction {
            public string FlowId { get; init; }
            public string UserId { get; init; }
            public string Email { get; init; }
            public string CredentialId { get; init; }
            public string CredentialName { get; init; }
            public string CredentialType { get; init; }
            public string CredentialIssuer { get; init; }
            public string AccountBindingClaim { get; init; }
            public string AccountBindingSource { get; init; }
            public string ExpectedAccountValue { get; init; }
            public string ProfileValueClaim { get; init; }
            public string Status { get; set; } = "ready";
            public string Message { get; set; } = "Present your selected credential";
            public string RequestId { get; set; }
            public string ProfileValue { get; set; }
            public bool ProviderReturnSeen { get; set; }
        }

        public void Add(string state, Transaction transaction, TimeSpan? lifetime = null) {
            _cache.Set(state, transaction, lifetime ?? TimeSpan.FromHours(1));
        }

        public bool TryGet(string state, out Transaction transaction) =>
            _cache.TryGetValue(state, out transaction);

        public void Remove(string state) => _cache.Remove(state);
    }
}
