﻿using System.Threading;
using System.Threading.Tasks;

namespace PushNotifications.Google.Legacy
{
    public interface IFcmClient
    {
        Task<FcmResponse> SendAsync(FcmRequest request, CancellationToken ct = default);
    }
}