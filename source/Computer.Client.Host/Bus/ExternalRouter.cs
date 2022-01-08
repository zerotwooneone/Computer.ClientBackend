﻿using System.Collections.Concurrent;
using System.Reactive.Linq;
using Computer.Bus.Domain.Contracts;
using InternalBus = Computer.Domain.Bus.Reactive.Contracts.IReactiveBus;
using IExternalBus = Computer.Bus.Domain.Contracts.IBus;
using InternalEvents = Computer.Client.Host.App.Events;
using ExternalEvents = Computer.Client.Domain.Bus.Events;
using ExternalModels = Computer.Client.Domain.Model;
using InternalModels = Computer.Client.Host.App;
using InternalBusEvent = Computer.Domain.Bus.Reactive.Contracts.Model.IBusEvent;

namespace Computer.Client.Host.Bus;

public class ExternalRouter
{
    private readonly InternalBus _internalBus;
    private readonly IExternalBus _externalBus;

    //private readonly ConcurrentDictionary<string, ExternalToInternalConfig> _externalToInternal;
    private readonly ConcurrentDictionary<string, InternalToExternalConfig> _internalToExternal = new();
    private readonly List<IDisposable> _subscriptions = new();

    public ExternalRouter(
        InternalBus internalBus,
        IExternalBus externalBus)
    {
        _internalBus = internalBus;
        _externalBus = externalBus;

        // _externalToInternal = new(new Dictionary<string, ExternalToInternalConfig>
        // {
        //     {Events.GetConnectionResponse, new ExternalToInternalConfig()},
        //     {Events.CloseConnectionResponse, new ExternalToInternalConfig()},
        // });

        _internalToExternal = new ConcurrentDictionary<string, InternalToExternalConfig>(
            new Dictionary<string, InternalToExternalConfig>
            {
                { InternalEvents.GetConnection, new InternalToExternalConfig(OnGetConnection, typeof(InternalModels.AppConnectionRequest)) }
                //{InternalEvents.CloseConnection, new InternalToExternalConfig(OnDisconnectRequest)},
            });
    }

    public async Task RestartListening()
    {
        await StopListening();
        var extenalSubs = new List<Task<Computer.Bus.Domain.Contracts.ISubscription>>();
        extenalSubs.AddRange(new[]
        {
            _externalBus.Subscribe<ExternalModels.AppConnectionResponse>(ExternalEvents.GetConnectionResponse,
                OnConnectionResponse)
            //_externalBus.Subscribe<ExternalModels.AppDisconnectResponse>(ExternalEvents.CloseConnectionResponse, OnCloseResponse),
        });
        var subscriptions = await Task.WhenAll(extenalSubs);
        _subscriptions.AddRange(subscriptions);

        var internalSubs = _internalToExternal.Select(internalToExtenalKvp =>
        {
            return _internalBus.Subscribe(internalToExtenalKvp.Key, internalToExtenalKvp.Value.InternalSubscribeType)
                .SelectMany(busEvent => Observable.FromAsync(async _ => await OnInternalEvent(busEvent, internalToExtenalKvp)))
                .Subscribe();
        });
        _subscriptions.AddRange(internalSubs);
    }

    private async Task OnInternalEvent(InternalBusEvent busEvent,
        KeyValuePair<string, InternalToExternalConfig> internalToExtenalKvp)
    {
        var result = await internalToExtenalKvp.Value.InternalToExternalCallback(internalToExtenalKvp.Key, busEvent);
    }

    private async Task<Computer.Bus.Domain.Contracts.IPublishResult> OnGetConnection(string subject, InternalBusEvent busEvent)
    {
        if (busEvent.Param == null)
        {
            return Computer.Bus.Domain.Contracts.PublishResult.CreateError(
                "failed trying to publish/route. param was null");
        }

        if (!typeof(InternalModels.AppConnectionRequest).IsAssignableFrom(busEvent.Type))
        {
            return Computer.Bus.Domain.Contracts.PublishResult.CreateError(
                "failed trying to publish/route. types do not match");
        }

        var param = (InternalModels.AppConnectionRequest)busEvent.Param;
        if (param.instanceId == null)
        {
            return Computer.Bus.Domain.Contracts.PublishResult.CreateError(
                "failed trying to publish/route. instance id was null");
        }
        return await _externalBus.Publish<ExternalModels.AppConnectionRequest>(ExternalEvents.GetConnection,
            new ExternalModels.AppConnectionRequest { AppId = param.appId, InstanceId = param.instanceId },
            null,
            busEvent.CorrelationId);
    }

    /*private async Task<IPublishResult> OnDisconnectRequest(string subject, BusEvent busEvent)
    {
        if (busEvent.Param == null)
        {
            return PublishResult.CreateError("failed trying to publish/route. param was null");
        }
        if (busEvent.Type != typeof(InternalModels.AppDisconnectRequest))
        {
            return PublishResult.CreateError("failed trying to publish/route. types do not match");
        }

        var param = (InternalModels.AppDisconnectRequest)busEvent.Param;
        return await _externalBus.Publish(ExternalEvents.CloseConnection,
            new ExternalModels.AppDisconnectRequest { AppId = param.appId, InstanceId = param.instanceId },
            eventId: null,
            correlationId: busEvent.CorrelationId);
    }*/

    public Task StopListening()
    {
        var subscriptions = _subscriptions.ToArray();
        _subscriptions.Clear();
        foreach (var subscription in subscriptions)
        {
            if (subscription == null) continue;

            try
            {
                subscription.Dispose();
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }
        return Task.CompletedTask;
    }

    private async Task OnConnectionResponse(ExternalModels.AppConnectionResponse? param, string eventId,
        string correlationId)
    {
        if (param == null) return;
        await _internalBus.Publish(
            InternalEvents.GetConnectionResponse,
            typeof(InternalModels.AppConnectionResponse),
            new InternalModels.AppConnectionResponse(param.InstanceId),
            null,
            correlationId);
    }

    // private async Task OnCloseResponse(ExternalModels.AppDisconnectResponse? param, string eventid, string correlationid)
    // {
    //     if(param == null) return;
    //     await _internalBus.Publish(
    //         InternalEvents.CloseConnectionResponse,
    //         new InternalModels.AppConnectionResponse("disconnected without id"),
    //         eventId: null,
    //         correlationid);
    // }

    //private record ExternalToInternalConfig();

    private record InternalToExternalConfig(
        Func<string, InternalBusEvent, Task<Computer.Bus.Domain.Contracts.IPublishResult>> InternalToExternalCallback,
        Type InternalSubscribeType);
}