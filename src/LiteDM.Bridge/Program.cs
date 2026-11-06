using LiteDM.Core;

namespace LiteDM.Bridge;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var origin = args.FirstOrDefault(argument => argument.StartsWith("chrome-extension://", StringComparison.Ordinal));
            if (!NativeMessaging.IsAllowedOrigin(origin, NativeMessaging.LoadAllowedOrigins()))
            {
                await NativeMessaging.WriteMessageAsync(Console.OpenStandardOutput(), new NativeResponse(false,
                    "LiteDM rejected this browser extension origin. Register the LiteDM extension and try again."));
                return 0;
            }
            var requestBytes = await NativeMessaging.ReadMessageAsync(Console.OpenStandardInput(), NativeMessaging.MaxMessageBytes);
            if (requestBytes is null) return 0;
            var response = await NativeMessaging.HandleMessageAsync(requestBytes);
            await NativeMessaging.WriteMessageAsync(Console.OpenStandardOutput(), response);
            return 0;
        }
        catch (InvalidDataException ex)
        {
            await NativeMessaging.WriteMessageAsync(Console.OpenStandardOutput(), new NativeResponse(false, ex.Message));
            return 0;
        }
        catch (EndOfStreamException)
        {
            await NativeMessaging.WriteMessageAsync(Console.OpenStandardOutput(), new NativeResponse(false, "The browser sent a truncated message. Try again."));
            return 0;
        }
        catch
        {
            // The browser owns stdout as a binary protocol stream. Never write diagnostics there.
            return 1;
        }
    }
}
