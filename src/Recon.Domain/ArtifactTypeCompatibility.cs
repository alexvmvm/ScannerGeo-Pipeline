using System.IO;

namespace Recon.Domain;

public static class ArtifactTypeCompatibility
{
    public static ArtifactType GetEffectiveType(Artifact artifact)
        => GetEffectiveType(artifact.Type, artifact.FileName, artifact.MimeType, artifact.StorageKey);

    public static ArtifactType GetEffectiveType(
        ArtifactType storedType,
        string? fileName,
        string? mimeType = null,
        string? storageKey = null)
    {
        var normalizedFileName = NormalizeFileName(fileName, storageKey);
        if (normalizedFileName.Length > 0)
        {
            switch (normalizedFileName)
            {
                case "inspect.json":
                    return ArtifactType.InspectReport;
                case "sparse-model.zip":
                    return ArtifactType.SparseModel;
                case "fused.ply":
                    return ArtifactType.DensePointCloud;
                case "dense.json":
                    return ArtifactType.DenseReport;
                case "sparse.json":
                    return ArtifactType.SparseReport;
                case "export.json":
                    return ArtifactType.ExportReport;
                case "publish.json":
                    return ArtifactType.PublishReport;
                case "octree-scene-package.zip":
                    return ArtifactType.OctreePackage;
                case "export-package.zip":
                    return ArtifactType.ExportPackage;
                case "dense-visibility-package.zip":
                    return ArtifactType.DenseVisibilityPackage;
                case "summary.json":
                    return ArtifactType.SummaryJson;
            }

            if (normalizedFileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            {
                return ArtifactType.LogFile;
            }
        }

        if (string.Equals(mimeType, "text/plain", StringComparison.OrdinalIgnoreCase)
            && string.Equals(storedType.ToString(), nameof(ArtifactType.SummaryJson), StringComparison.Ordinal))
        {
            return ArtifactType.LogFile;
        }

        return storedType;
    }

    public static bool Matches(Artifact artifact, ArtifactType expectedType)
        => GetEffectiveType(artifact) == expectedType;

    private static string NormalizeFileName(string? fileName, string? storageKey)
    {
        var value = !string.IsNullOrWhiteSpace(fileName)
            ? fileName
            : storageKey;
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return Path.GetFileName(value.Replace('\\', '/')).Trim().ToLowerInvariant();
    }
}
