﻿using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Text.Json;
using Computer.Client.App.Hubs;
using Computer.Client.App.Model;
using Computer.Client.Domain.Contracts.Bus;
using Computer.Client.Domain.Contracts.Model;
using Computer.Domain.Bus.Reactive.Contracts;
using Computer.Domain.Bus.Reactive.Contracts.Model;
using Microsoft.AspNetCore.SignalR;

namespace Computer.Client.App.Bus;

public class HubRouter : IEventHandler, IHubRouter
{
    private readonly IReactiveBus bus;
    private readonly IHubContext<BusHub, IBusHub> _busHub;
    private readonly ConcurrentDictionary<string, SubjectConfig> _fromUiToBackend;

    private static object DirectConvert(Type type, JsonElement from)
    {
        return Convert.ChangeType(from, type);
    }

    private readonly ConcurrentDictionary<string, SubjectConfig> fromBackendToUi = new(
        new Dictionary<string, SubjectConfig>
        {
            { "subject name", new SubjectConfig(typeof(int), DirectConvert) },
            {
                Events.GetConnectionResponse,
                new SubjectConfig(typeof(AppConnectionResponse),
                    (type, from) => { throw new NotImplementedException(); })
            },
            { Events.CloseConnectionResponse, new SubjectConfig() }
        });

    private IEnumerable<IDisposable> _subscriptions = Enumerable.Empty<IDisposable>();

    public HubRouter(
        IReactiveBus bus,
        IHubContext<BusHub, IBusHub> busHub)
    {
        this.bus = bus;
        _busHub = busHub;

        _fromUiToBackend = new ConcurrentDictionary<string, SubjectConfig>(new Dictionary<string, SubjectConfig>
        {
            { "subject name", new SubjectConfig(typeof(int), DirectConvert) },
            {
                Events.GetConnection, new SubjectConfig(typeof(AppConnectionRequest), (type, from) =>
                {
                    var o = JsonSerializer.Deserialize<AppConnectionRequest>(from,
                        HostJsonContext.Default.AppConnectionRequest);
                    return o;
                })
            },
            // {
            //     Events.CloseConnection, new SubjectConfig(typeof(AppDisconnectRequest), (type, from) =>
            //     {
            //         var o = JsonSerializer.Deserialize<AppDisconnectRequest>(from,
            //             HostJsonContext.Default.AppDisconnectRequest);
            //         return o;
            //     })
            // }
        });
    }

    public void ReStartListening()
    {
        StopListening();
        var subs = new List<IDisposable>();
        foreach (var subject in fromBackendToUi)
        {
            var subscription = subject.Value.type == null
                ? bus.Subscribe(subject.Key)
                    .SelectMany(e => Observable.FromAsync(async _=>await ConvertToHubEvent(subject.Key,e).ConfigureAwait(false)) )
                    .Subscribe()
                : bus.Subscribe(subject.Key, subject.Value.type)
                    .SelectMany(e => Observable.FromAsync(async _=>await ConvertToHubEvent(subject.Key,e).ConfigureAwait(false)) )
                    .Subscribe();

            subs.Add(subscription);
        }

        _subscriptions = subs;
    }

    public void StopListening()
    {
        foreach (var subscription in _subscriptions)
            try
            {
                subscription.Dispose();
            }
            catch
            {
                //nothing, we just dont want to fail while disposing
            }

        _subscriptions = Enumerable.Empty<IDisposable>();
    }

    private async Task ConvertToHubEvent(string subject, IBareEvent busEvent)
    {
        var @event = new EventForFrontEnd(subject, busEvent.EventId, busEvent.CorrelationId, null);
        await _busHub.Clients.All.EventToFrontEnd(@event).ConfigureAwait(false);
    }
    private async Task ConvertToHubEvent(string subject, IBusEvent busEvent)
    {
        var @event = new EventForFrontEnd(subject, busEvent.EventId, busEvent.CorrelationId, busEvent.Param);
        await _busHub.Clients.All.EventToFrontEnd(@event).ConfigureAwait(false);
    }

    public Task HandleBackendEvent(string subject, string eventId, string correlationId, JsonElement? eventObj = null)
    {
        if (_fromUiToBackend.TryGetValue(subject, out var config))
        {
            if (config.type is null)
            {
                bus.Publish(subject);
            }
            else
            {
                if (eventObj != null && config.ConvertFromHub != null)
                {
                    var obj = config.ConvertFromHub(config.type, (JsonElement)eventObj);
                    bus.Publish(subject, config.type, obj, eventId, correlationId);
                }
            }
        }

        return Task.CompletedTask;
    }

    public void Test()
    {
        bus.Publish<string>("subject name", "it works");
    }
}