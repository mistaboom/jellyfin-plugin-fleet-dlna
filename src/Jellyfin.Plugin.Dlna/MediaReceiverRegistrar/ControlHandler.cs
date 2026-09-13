using System;
using System.Collections.Generic;
using System.Xml;
using Jellyfin.Plugin.Dlna.Service;
using MediaBrowser.Common.Extensions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Dlna.MediaReceiverRegistrar;

/// <summary>
/// Defines the <see cref="ControlHandler" />.
/// </summary>
public class ControlHandler : BaseControlHandler
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ControlHandler"/> class.
    /// </summary>
    /// <param name="logger">The <see cref="ILogger"/>.</param>
    public ControlHandler(ILogger logger)
        : base(logger)
    {
    }

    /// <inheritdoc />
    protected override void WriteResult(string methodName, IReadOnlyDictionary<string, string> methodParams, XmlWriter xmlWriter)
    {
        if (string.Equals(methodName, "IsAuthorized", StringComparison.OrdinalIgnoreCase))
        {
            HandleIsAuthorized(xmlWriter);
            return;
        }

        if (string.Equals(methodName, "IsValidated", StringComparison.OrdinalIgnoreCase))
        {
            HandleIsValidated(xmlWriter);
            return;
        }

        // Every client is authorized and validated, so none of the counters that track a change of
        // that ever moves. Answering zero is what a service that never revokes anything reports.
        if (string.Equals(methodName, "GetValidationSucceededUpdateID", StringComparison.OrdinalIgnoreCase))
        {
            xmlWriter.WriteElementString("ValidationSucceededUpdateID", "0");
            return;
        }

        if (string.Equals(methodName, "GetValidationRevokedUpdateID", StringComparison.OrdinalIgnoreCase))
        {
            xmlWriter.WriteElementString("ValidationRevokedUpdateID", "0");
            return;
        }

        if (string.Equals(methodName, "GetAuthorizationGrantedUpdateID", StringComparison.OrdinalIgnoreCase))
        {
            xmlWriter.WriteElementString("AuthorizationGrantedUpdateID", "0");
            return;
        }

        if (string.Equals(methodName, "GetAuthorizationDeniedUpdateID", StringComparison.OrdinalIgnoreCase))
        {
            xmlWriter.WriteElementString("AuthorizationDeniedUpdateID", "0");
            return;
        }

        throw new ResourceNotFoundException("Unexpected control request name: " + methodName);
    }

    /// <summary>
    /// Records that the handle is authorized in the xml stream.
    /// </summary>
    /// <param name="xmlWriter">The <see cref="XmlWriter"/>.</param>
    private static void HandleIsAuthorized(XmlWriter xmlWriter)
        => xmlWriter.WriteElementString("Result", "1");

    /// <summary>
    /// Records that the handle is validated in the xml stream.
    /// </summary>
    /// <param name="xmlWriter">The <see cref="XmlWriter"/>.</param>
    private static void HandleIsValidated(XmlWriter xmlWriter)
        => xmlWriter.WriteElementString("Result", "1");
}
