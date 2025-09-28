using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace LiteDB.ReproRunner.Cli.Infrastructure;

/// <summary>
/// Bridges Spectre.Console type registration to Microsoft.Extensions.DependencyInjection.
/// </summary>
internal sealed class TypeRegistrar : ITypeRegistrar
{
    private readonly IServiceCollection _services = new ServiceCollection();

    /// <summary>
    /// Registers a service implementation type.
    /// </summary>
    /// <param name="service">The service contract.</param>
    /// <param name="implementation">The implementation type.</param>
    public void Register(Type service, Type implementation)
    {
        if (service is null)
        {
            throw new ArgumentNullException(nameof(service));
        }

        if (implementation is null)
        {
            throw new ArgumentNullException(nameof(implementation));
        }

        _services.AddSingleton(service, implementation);
    }

    /// <summary>
    /// Registers a specific service instance.
    /// </summary>
    /// <param name="service">The service contract.</param>
    /// <param name="implementation">The implementation instance.</param>
    public void RegisterInstance(Type service, object implementation)
    {
        if (service is null)
        {
            throw new ArgumentNullException(nameof(service));
        }

        if (implementation is null)
        {
            throw new ArgumentNullException(nameof(implementation));
        }

        _services.AddSingleton(service, implementation);
    }

    /// <summary>
    /// Registers a lazily constructed service implementation.
    /// </summary>
    /// <param name="service">The service contract.</param>
    /// <param name="factory">The factory used to create the implementation.</param>
    public void RegisterLazy(Type service, Func<object> factory)
    {
        if (service is null)
        {
            throw new ArgumentNullException(nameof(service));
        }

        if (factory is null)
        {
            throw new ArgumentNullException(nameof(factory));
        }

        _services.AddSingleton(service, _ => factory());
    }

    /// <summary>
    /// Builds the resolver that Spectre.Console uses to resolve services.
    /// </summary>
    /// <returns>The type resolver backed by the configured service provider.</returns>
    public ITypeResolver Build()
    {
        return new TypeResolver(_services.BuildServiceProvider());
    }

    private sealed class TypeResolver : ITypeResolver, IDisposable
    {
        private readonly ServiceProvider _provider;

        /// <summary>
        /// Initializes a new instance of the <see cref="TypeResolver"/> class.
        /// </summary>
        /// <param name="provider">The underlying service provider.</param>
        public TypeResolver(ServiceProvider provider)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        }

        /// <summary>
        /// Resolves an instance of the requested service type.
        /// </summary>
        /// <param name="type">The service type to resolve.</param>
        /// <returns>The resolved instance or <c>null</c> when unavailable.</returns>
        public object? Resolve(Type? type)
        {
            return type is null ? null : _provider.GetService(type);
        }

        /// <summary>
        /// Releases resources associated with the service provider.
        /// </summary>
        public void Dispose()
        {
            _provider.Dispose();
        }
    }
}
