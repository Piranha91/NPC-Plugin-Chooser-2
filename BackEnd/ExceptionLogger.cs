namespace NPC_Plugin_Chooser_2.BackEnd;

using System.Text;
using Mutagen.Bethesda.Plugins.Exceptions;

public class ExceptionLogger
{
    public static string GetExceptionStack(Exception e)
    {
        var error = new StringBuilder();
        AppendException(e, error, 0, "");
        return error.ToString();
    }

    private static void AppendException(Exception e, StringBuilder error, int layer, string branch)
    {
        // ReactiveUI's wrapper adds no application-specific information.
        if (e is not ReactiveUI.UnhandledErrorException || e.InnerException is null)
        {
            var branchLabel = string.IsNullOrEmpty(branch) ? "" : $" (branch {branch})";
            error.AppendLine().AppendLine($"======= Layer {layer}{branchLabel}:")
                .AppendLine($"{e.GetType().FullName}: {e.Message}");

            // Mutagen stores this context separately from Message and StackTrace.
            if (e is RecordException recordException)
            {
                if (recordException.ModKey is { } modKey)
                    error.AppendLine($"Plugin: {modKey.FileName}");
                if (recordException.FormKey is { } formKey)
                    error.AppendLine($"Record: {formKey}");
                if (recordException.RecordType is { } recordType)
                    error.AppendLine($"Record type: {recordType.FullName}");
                if (!string.IsNullOrWhiteSpace(recordException.EditorID))
                    error.AppendLine($"Editor ID: {recordException.EditorID}");
            }

            if (e is ModGroupsMalformedException { ModPath: { } modPath })
            {
                error.AppendLine($"Plugin: {modPath.ModKey.FileName}")
                    .AppendLine($"Plugin path: {modPath.Path}");
            }

            if (e is TooManyMastersException tooManyMastersException)
            {
                error.AppendLine($"Plugin: {tooManyMastersException.CurrentMod.FileName}")
                    .AppendLine("Current Masters:");
                foreach (var master in tooManyMastersException.Masters)
                    error.AppendLine(master.FileName.ToString());
            }

            error.AppendLine(e.StackTrace).AppendLine();
        }

        if (e is AggregateException aggregateException)
        {
            // InnerException exposes only the first failure. Walk every branch without
            // flattening away the wrappers that carry plugin context.
            for (var i = 0; i < aggregateException.InnerExceptions.Count; i++)
            {
                var childBranch = string.IsNullOrEmpty(branch) ? $"{i + 1}" : $"{branch}.{i + 1}";
                AppendException(aggregateException.InnerExceptions[i], error, layer + 1, childBranch);
            }
        }
        else if (e.InnerException != null)
        {
            AppendException(e.InnerException, error, layer + 1, branch);
        }
    }
}
