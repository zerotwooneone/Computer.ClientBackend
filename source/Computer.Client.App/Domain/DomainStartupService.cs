﻿using Computer.Bus.Domain;
using Computer.Bus.Domain.Contracts;
using Computer.Client.App.Domain.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Computer.Client.App.Domain;

public class DomainStartupService : IHostedService
{
    private readonly DomainMapRegistrationService _domainMapRegistrationService;
    private readonly IOptions<BusConfig> _busConfig;
    private readonly Initializer _initializer;

    public DomainStartupService(DomainMapRegistrationService domainMapRegistrationService,
        IOptions<BusConfig> busConfig,
        Initializer initializer)
    {
        _domainMapRegistrationService = domainMapRegistrationService;
        _busConfig = busConfig;
        _initializer = initializer;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_busConfig.Value?.Subjects == null) throw new InvalidDataException("Bus config subjects not found");
        if (_busConfig.Value?.Maps == null) throw new InvalidDataException("Bus config maps not found");
        var maps = _busConfig.Value.Maps
            .Aggregate(new List<MapRegistration>(), (list, config) =>
            {
                if (config == null ||
                    string.IsNullOrWhiteSpace(config.Domain) ||
                    string.IsNullOrWhiteSpace(config.Dto) ||
                    string.IsNullOrWhiteSpace(config.Mapper))
                    return list;

                var domain = Type.GetType(config.Domain);
                var dto = Type.GetType(config.Dto);
                var mapper = Type.GetType(config.Mapper);
                if (domain == null ||
                    dto == null ||
                    mapper == null)
                    return list;

                var mapRegistration = new MapRegistration(domain, dto, mapper);
                list.Add(mapRegistration);
                return list;
            });
        var subjects = _busConfig.Value.Subjects.Aggregate(new List<ISubjectRegistration>(), (list, config) =>
        {
            var type = string.IsNullOrWhiteSpace(config.Value)
                ? null
                : Type.GetType(config.Value);
            list.Add(new SubjectRegistration(config.Key, type));
            return list;
        });

        _initializer.Register(subjects, maps);
        var mapperTypes = maps.Select(m => m.Mapper);
        _domainMapRegistrationService.Register(mapperTypes);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

internal record MapRegistration(Type Domain, Type Dto, Type Mapper) : IMapRegistration;

internal record SubjectRegistration(string SubjectName, Type? Type) : ISubjectRegistration;