using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SonicRoute.Updates
{
    internal sealed partial class UpdateService
    {
        internal UpdateService(Func<HttpMessageHandler>? handlerFactory = null)
            : this(App.DisplayVersion, AppRuntimeIdentity.Current.CommandEdition, Dispatch, handlerFactory: handlerFactory) { }

        private static Task Dispatch(Action action, CancellationToken token)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) { action(); return Task.CompletedTask; }
            return dispatcher.InvokeAsync(action, System.Windows.Threading.DispatcherPriority.Normal, token).Task;
        }
    }
}
