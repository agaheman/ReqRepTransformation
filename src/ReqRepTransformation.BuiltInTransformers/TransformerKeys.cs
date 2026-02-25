namespace ReqRepTransformation.BuiltInTransformers;

/// <summary>
/// String constants for keyed DI service registration and resolution.
///
/// These keys are the contract between:
///   1. <c>BuiltInTransformersServiceCollectionExtensions.AddBuiltInTransformers()</c>
///      which calls <c>services.AddKeyedTransient&lt;ITransformer, TImpl&gt;(key)</c>
///   2. <see cref="Core.Models.RouteTransformerEntry.TransformerKey"/> loaded from the database.
///   3. <c>TransformationDetailBuilder</c> which resolves instances via
///      <c>sp.GetRequiredKeyedService&lt;ITransformer&gt;(key)</c>.
///
/// Store these values in the database column <c>transformer_key</c>.
/// </summary>
public static class TransformerKeys
{
    // ── Headers ───────────────────────────────────────────────────────────
    public const string AddHeader                     = "add-header";
    public const string RemoveHeader                  = "remove-header";
    public const string RenameHeader                  = "rename-header";
    public const string AppendHeader                  = "append-header";
    public const string CorrelationId                 = "correlation-id";
    public const string RequestId                     = "request-id";
    public const string RemoveInternalResponseHeaders = "remove-internal-response-headers";
    public const string GatewayResponseTag            = "gateway-response-tag";
    public const string UploadMetadataHeader          = "upload-metadata-header";

    // ── Address / URI ─────────────────────────────────────────────────────
    public const string PathPrefixRewrite             = "path-prefix-rewrite";
    public const string PathRegexRewrite              = "path-regex-rewrite";
    public const string AddQueryParam                 = "add-query-param";
    public const string RemoveQueryParam              = "remove-query-param";
    public const string HostRewrite                   = "host-rewrite";
    public const string MethodOverride                = "method-override";

    // ── JSON Body ─────────────────────────────────────────────────────────
    public const string JsonFieldAdd                  = "json-field-add";
    public const string JsonFieldRemove               = "json-field-remove";
    public const string JsonFieldRename               = "json-field-rename";
    public const string JsonNestedFieldSet            = "json-nested-field-set";
    public const string JsonGatewayMetadata           = "json-gateway-metadata";

    // ── Auth / JWT ────────────────────────────────────────────────────────
    public const string JwtForward                    = "jwt-forward";
    public const string JwtClaimsExtract              = "jwt-claims-extract";
    public const string StripAuthorization            = "strip-authorization";
}
