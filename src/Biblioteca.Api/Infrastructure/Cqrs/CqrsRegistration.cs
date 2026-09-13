using System.Reflection;
using Biblioteca.Api.Infrastructure.Caching;
using Biblioteca.Api.Infrastructure.Cqrs.Decorators;

namespace Biblioteca.Api.Infrastructure.Cqrs;

public static class CqrsRegistration
{
    /// <summary>
    /// Registra o dispatcher, os handlers encontrados por scan em <paramref name="assemblies"/>
    /// e monta o pipeline via <c>Scrutor.Decorate</c> — sem MediatR (ADR-0002).
    ///
    /// Ordem de registro importa: cada chamada a <c>Decorate</c> embrulha a anterior, então
    /// a <b>última</b> chamada vira a camada mais externa. Para obter
    /// Metrics → Validation → CacheInvalidation → Transaction → Idempotency → Handler
    /// (docs/architecture.md#o-pipeline-cqrs), registramos na ordem inversa da execução.
    /// </summary>
    public static IServiceCollection AddCqrs(this IServiceCollection services, params Assembly[] assemblies)
    {
        services.AddScoped<IDispatcher, Dispatcher>();
        services.AddScoped<ICacheInvalidationQueue, CacheInvalidationQueue>();

        // publicOnly: false — handlers ficam internal por padrão (só o Dispatcher os usa
        // via DI); o scan padrão do Scrutor ignoraria classes não-públicas.
        services.Scan(scan => scan
            .FromAssemblies(assemblies)
            .AddClasses(classes => classes.AssignableToAny(typeof(ICommandHandler<,>), typeof(IQueryHandler<,>)), publicOnly: false)
            .AsImplementedInterfaces()
            .WithScopedLifetime());

        // Comandos
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(IdempotencyCommandDecorator<,>));
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(TransactionCommandDecorator<,>));
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(CacheInvalidationCommandDecorator<,>));
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(ValidationCommandDecorator<,>));
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(MetricsCommandDecorator<,>));

        // Queries
        services.TryDecorate(typeof(IQueryHandler<,>), typeof(ValidationQueryDecorator<,>));
        services.TryDecorate(typeof(IQueryHandler<,>), typeof(MetricsQueryDecorator<,>));

        return services;
    }
}
