using System.Text.Json.Nodes;
using AutoCadMcp.Protocol;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcadException = Autodesk.AutoCAD.Runtime.Exception;

namespace AutoCadMcp.Plugin;

/// <summary>Turns pipe requests into drawing operations. Must run on AutoCAD's main thread.</summary>
internal static class CommandRouter
{
    public static PipeResponse Execute(PipeRequest request)
    {
        try
        {
            if (request.Command == "status")
                return PipeResponse.Success(DrawingCommands.Status());

            if (!DrawingCommands.Handlers.TryGetValue(request.Command, out var handler))
                return PipeResponse.Failure($"Unknown command '{request.Command}'.");

            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc is null)
                return PipeResponse.Failure("No drawing is open in AutoCAD.");

            // We run outside any command, so never interleave with one the user has started.
            if (!doc.Editor.IsQuiescent)
            {
                var busyWith = doc.CommandInProgress is { Length: > 0 } name ? $"the {name} command" : "another operation";
                return PipeResponse.Failure($"AutoCAD is busy with {busyWith}. Finish or cancel it (Esc) and try again.");
            }

            JsonNode? result;
            using (doc.LockDocument())
            using (var tr = doc.TransactionManager.StartTransaction())
            {
                result = handler(new DrawingContext(doc, tr), request.Args ?? new JsonObject());
                doc.TransactionManager.QueueForGraphicsFlush();
                tr.Commit();
            }
            doc.Editor.UpdateScreen();
            return PipeResponse.Success(result);
        }
        catch (CommandException ex)
        {
            return PipeResponse.Failure(ex.Message);
        }
        catch (AcadException ex)
        {
            return PipeResponse.Failure($"AutoCAD rejected the operation ({ex.ErrorStatus}).");
        }
        catch (Exception ex)
        {
            return PipeResponse.Failure($"{ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>An error caused by the request itself; its message is shown to the caller as-is.</summary>
internal sealed class CommandException(string message) : Exception(message);
