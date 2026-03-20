using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Recon.Api.Swagger;

public sealed class EnumDescriptionSchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        var enumType = Nullable.GetUnderlyingType(context.Type) ?? context.Type;
        if (!enumType.IsEnum)
        {
            return;
        }

        var values = Enum.GetNames(enumType)
            .Select(name => $"{name} ({Convert.ToInt32(Enum.Parse(enumType, name))})");
        var description = $"Allowed values: {string.Join(", ", values)}.";
        schema.Description = string.IsNullOrWhiteSpace(schema.Description)
            ? description
            : $"{schema.Description} {description}";
    }
}

public sealed class ReconExamplesOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var method = context.ApiDescription.HttpMethod?.ToUpperInvariant();
        var path = "/" + context.ApiDescription.RelativePath?.TrimStart('/');
        if (method is null || path is null)
        {
            return;
        }

        ApplyRequestExamples(operation, method, path);
        ApplyResponseExamples(operation, method, path);
    }

    private static void ApplyRequestExamples(OpenApiOperation operation, string method, string path)
    {
        if (operation.RequestBody is null)
        {
            return;
        }

        if (method == "POST" && path == "/api/v1/projects")
        {
            SetJsonExample(operation.RequestBody, new OpenApiObject
            {
                ["name"] = new OpenApiString("Wapping Site Scan 01"),
                ["description"] = new OpenApiString("Initial scan of site"),
                ["externalReference"] = new OpenApiString("client-123"),
                ["ownerReference"] = new OpenApiString("owner-x"),
                ["siteReference"] = new OpenApiString("site-42"),
                ["sourceType"] = new OpenApiString("manual_upload"),
                ["config"] = new OpenApiObject
                {
                    ["matcherType"] = new OpenApiString("exhaustive"),
                    ["enablePotreeExport"] = new OpenApiBoolean(false)
                }
            });
            return;
        }

        if (method == "POST" && path == "/api/v1/projects/{projectId:guid}/imports")
        {
            SetJsonExample(operation.RequestBody, new OpenApiObject
            {
                ["urls"] = new OpenApiArray
                {
                    new OpenApiString("https://example.com/photo1.jpg"),
                    new OpenApiString("https://example.com/photo2.jpg")
                }
            });
            return;
        }

        if (method == "POST" && path == "/api/v1/projects/{projectId:guid}/runs")
        {
            SetJsonExample(operation.RequestBody, new OpenApiObject
            {
                ["stages"] = new OpenApiArray
                {
                    new OpenApiString("Inspect"),
                    new OpenApiString("Sparse"),
                    new OpenApiString("Dense"),
                    new OpenApiString("Export")
                },
                ["forceRebuild"] = new OpenApiBoolean(false)
            });
            return;
        }

        if (method == "POST" && path == "/api/v1/projects/{projectId:guid}/images")
        {
            operation.RequestBody.Description = "Multipart upload of one or more image files. Use the form field name `files` for every uploaded file.";
            if (operation.RequestBody.Content.TryGetValue("multipart/form-data", out var multipart))
            {
                multipart.Schema = new OpenApiSchema
                {
                    Type = "object",
                    Required = new HashSet<string> { "files" },
                    Properties =
                    {
                        ["files"] = new OpenApiSchema
                        {
                            Type = "array",
                            Description = "One or more image files to upload.",
                            Items = new OpenApiSchema
                            {
                                Type = "string",
                                Format = "binary"
                            }
                        }
                    }
                };
            }
        }
    }

    private static void ApplyResponseExamples(OpenApiOperation operation, string method, string path)
    {
        if (method == "POST" && path == "/api/v1/projects" && operation.Responses.TryGetValue("201", out var createdProject))
        {
            SetJsonExample(createdProject, new OpenApiObject
            {
                ["id"] = new OpenApiString("11111111-1111-1111-1111-111111111111"),
                ["name"] = new OpenApiString("Wapping Site Scan 01"),
                ["description"] = new OpenApiString("Initial scan of site"),
                ["status"] = new OpenApiString("Draft"),
                ["externalReference"] = new OpenApiString("client-123"),
                ["ownerReference"] = new OpenApiString("owner-x"),
                ["siteReference"] = new OpenApiString("site-42"),
                ["sourceType"] = new OpenApiString("manual_upload"),
                ["createdAtUtc"] = new OpenApiString("2026-03-19T12:00:00Z"),
                ["updatedAtUtc"] = new OpenApiString("2026-03-19T12:00:00Z"),
                ["latestRun"] = new OpenApiNull(),
                ["totalImageCount"] = new OpenApiInteger(0),
                ["validImageCount"] = new OpenApiInteger(0),
                ["artifactCount"] = new OpenApiInteger(0)
            });
            return;
        }

        if (method == "POST" && path == "/api/v1/projects/{projectId:guid}/imports" && operation.Responses.TryGetValue("202", out var acceptedImport))
        {
            SetJsonExample(acceptedImport, new OpenApiObject
            {
                ["id"] = new OpenApiString("22222222-2222-2222-2222-222222222222"),
                ["status"] = new OpenApiString("Running"),
                ["requestedCount"] = new OpenApiInteger(2),
                ["succeededCount"] = new OpenApiInteger(0),
                ["failedCount"] = new OpenApiInteger(0),
                ["items"] = new OpenApiArray
                {
                    new OpenApiObject
                    {
                        ["id"] = new OpenApiString("33333333-3333-3333-3333-333333333333"),
                        ["sourceUrl"] = new OpenApiString("https://example.com/photo1.jpg"),
                        ["status"] = new OpenApiString("Pending"),
                        ["errorMessage"] = new OpenApiNull(),
                        ["projectImageId"] = new OpenApiNull()
                    }
                }
            });
            return;
        }

        if (method == "POST" && path == "/api/v1/projects/{projectId:guid}/runs" && operation.Responses.TryGetValue("202", out var acceptedRun))
        {
            SetJsonExample(acceptedRun, new OpenApiObject
            {
                ["id"] = new OpenApiString("44444444-4444-4444-4444-444444444444"),
                ["status"] = new OpenApiString("Queued"),
                ["pipelineVersion"] = new OpenApiString("colmap"),
                ["createdAtUtc"] = new OpenApiString("2026-03-19T12:05:00Z"),
                ["startedAtUtc"] = new OpenApiNull(),
                ["finishedAtUtc"] = new OpenApiNull()
            });
            return;
        }

        if (method == "GET" && path == "/api/v1/jobs/{jobId:guid}" && operation.Responses.TryGetValue("200", out var jobResponse))
        {
            SetJsonExample(jobResponse, new OpenApiObject
            {
                ["id"] = new OpenApiString("55555555-5555-5555-5555-555555555555"),
                ["type"] = new OpenApiString("ValidateUploadedImage"),
                ["status"] = new OpenApiString("Running"),
                ["projectId"] = new OpenApiString("11111111-1111-1111-1111-111111111111"),
                ["pipelineRunId"] = new OpenApiNull(),
                ["progressPercent"] = new OpenApiDouble(42.5),
                ["progressMessage"] = new OpenApiString("Inspecting image metadata"),
                ["createdAtUtc"] = new OpenApiString("2026-03-19T12:01:00Z"),
                ["startedAtUtc"] = new OpenApiString("2026-03-19T12:01:02Z"),
                ["finishedAtUtc"] = new OpenApiNull()
            });
        }
    }

    private static void SetJsonExample(OpenApiRequestBody requestBody, IOpenApiAny example)
    {
        if (requestBody.Content.TryGetValue("application/json", out var content))
        {
            content.Example = example;
        }
    }

    private static void SetJsonExample(OpenApiResponse response, IOpenApiAny example)
    {
        if (response.Content.TryGetValue("application/json", out var content))
        {
            content.Example = example;
        }
    }
}
