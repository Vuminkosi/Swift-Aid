using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;

namespace SwiftAid.Infrastructure.Persistence.Transactions;

/// <summary>Creates isolated per-lane authorization scopes for bounded
/// parallel per-case checks. Each lane resolves its own unit of work and
/// policy from a fresh scope, so lanes never share a DbContext or lease.</summary>
public sealed class EllaiCaseAuthScopeFactory(IServiceScopeFactory scopeFactory) : IEllaiCaseAuthScopeFactory
{
    public IEllaiCaseAuthScope CreateScope() => new Lane(scopeFactory.CreateScope());

    private sealed class Lane(IServiceScope scope) : IEllaiCaseAuthScope
    {
        public ICaseCommandUnitOfWork UnitOfWork =>
            scope.ServiceProvider.GetRequiredService<ICaseCommandUnitOfWork>();

        public ICaseAccessPolicy Policy =>
            scope.ServiceProvider.GetRequiredService<ICaseAccessPolicy>();

        public ValueTask DisposeAsync()
        {
            if (scope is IAsyncDisposable asyncScope)
            {
                return asyncScope.DisposeAsync();
            }

            scope.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
