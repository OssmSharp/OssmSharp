namespace OSSMDotNet.WebCommon;

/// <summary>Provides CSP hashes for reviewed inline styles emitted by the shared widget package.</summary>
public static class RetroWidgetAssets
{
    // Exact static Shadow DOM styles from the checksum-pinned private RetroWebUi.Blazor 0.2.0 package.
    /// <summary>Gets the reviewed inline style hashes required by the widget package.</summary>
    public static IReadOnlyList<string> InlineStyleHashes { get; } = Array.AsReadOnly<string>(
    [
        "0MSqSre4YMt3ih9RP27QKyk3/1Nw42LmdGFHZtfZU4o=",
        "4/kPjRBVgrZfVFSXhy+28UKTSpnqjaNij3Afc74Sitw=",
        "45lf2FK7FIKEyJLDBvIP48OueUqogLRDpHmgNhaW/XI=",
        "9hvTMOkPO48bwu/uSHD2z6xHs367nGgQwsjhqNvikBA=",
        "DNgx1IYoOpWVMkpFf+A5/4m/0JUxYNQRMAsDbrGUkC8=",
        "HdOsRObr2faek8A/EaYVdcQzr36orSj0hJdnyEBeVPk=",
        "jW0auyAZuHcg2DTZYK1cRbAn49gq6qSKrHA+MP1DiSg=",
        "qxAhz2x6iml0nqdIaap6e9QQl9tYexcWuVHkfcSnLaw=",
        "rH4aycq16S5MlKLeVIWMoOZyUq200RdyqqzuROtxxhc=",
        "tJjjLTAiDuw71xtcVvwlT8ix5O164uKes+X/sOrzPgs=",
        "w+VpB3xomfCyly0rTimoD/aZ51DF05sdJklXGR00Bx0="
    ]);
}
