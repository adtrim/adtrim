using System.Collections.ObjectModel;
using AdTrim.Encoders;
using AdTrim.Services;

namespace AdTrim.ViewModels;

public sealed record ExportAccelerationOption(string Id, string Name, HardwareAdapter? Adapter = null,
    double? Milliseconds = null, bool? Available = null)
{
    public bool IsFastest { get; init; }
    public string TimingLabel => Milliseconds is { } ms ? $"{ms / 1000:0.00}s" : Available == false ? "Unavailable" : "";
    public string DisplayName => Milliseconds is { } ms ? $"{Name} ({ms / 1000:0.00}s)"
        : Available == false ? Name + " (unavailable)" : Name;
    public static ExportAccelerationOption Automatic { get; } = new("automatic", "Automatic");
    public static ExportAccelerationOption Software { get; } = new("software", "Software");
}

public sealed partial class ExportDialogViewModel
{
    public ObservableCollection<ExportAccelerationOption> AccelerationOptions { get; } = new()
    { ExportAccelerationOption.Automatic, ExportAccelerationOption.Software };
    private ExportAccelerationOption _selectedAcceleration = ExportAccelerationOption.Automatic;
    public ExportAccelerationOption SelectedAcceleration
    {
        get => _selectedAcceleration;
        set
        {
            if (value is null || !Set(ref _selectedAcceleration, value)) return;
            Notify(nameof(EncoderDescription));
            Notify(nameof(AccelerationLabel));
            Notify(nameof(EvaluationReport));
        }
    }
    private EncoderDetection? _detection;
    private bool _isCheckingHardware;
    public bool IsCheckingHardware
    {
        get => _isCheckingHardware;
        private set
        {
            Set(ref _isCheckingHardware, value);
            Notify(nameof(CanCheckHardware));
            Notify(nameof(CanOpenResults));
            Notify(nameof(HasFastest));
            Notify(nameof(FastestResult));
            Notify(nameof(EvaluationReport));
            Notify(nameof(EncoderDescription));
            Notify(nameof(AccelerationLabel));
        }
    }
    public bool CanCheckHardware => !IsCheckingHardware && !IsListingHardware;
    public bool CanOpenResults => IsCheckingHardware || _detection is not null;
    public bool HasFastest => !IsCheckingHardware && AccelerationOptions.Any(o => o.IsFastest);
    public string FastestResult => AccelerationOptions.FirstOrDefault(o => o.IsFastest)?.DisplayName ?? "";
    public string AccelerationLabel => IsCheckingHardware ? "Evaluating options..." : SelectedAcceleration.DisplayName;
    private bool _showEvaluation;
    public bool ShowEvaluation { get => _showEvaluation; set => Set(ref _showEvaluation, value); }
    public string EvaluationReport
    {
        get
        {
            if (IsCheckingHardware) return "Evaluating available encoders...\n\nYour selection will stay unchanged.";
            if (_detection is null) return "No evaluation results yet.";
            var fastest = _detection.Preferred?.Adapter.Name
                ?? (_detection.SoftwareMilliseconds is not null ? "Software" : null);
            var lines = new List<string> { fastest is null ? "No encoder passed evaluation." : "Fastest in this test: " + fastest };
            if (_detection.Error is { } error) lines.Add(error);
            lines.AddRange(_detection.Results.Select(r => r.Available
                ? $"{r.Adapter.Name}\n{r.Milliseconds / 1000:0.00}s - Passed"
                : $"{r.Adapter.Name}\nUnavailable: {r.Detail}"));
            lines.Add(_detection.SoftwareMilliseconds is { } ms ? $"Software\n{ms / 1000:0.00}s - Passed"
                : "Software\nUnavailable: " + _detection.SoftwareDetail);
            lines.Add("Times measure the test sample, not your episode. Actual export speed varies by video. Your selection has not changed.");
            return string.Join("\n\n", lines);
        }
    }
    private string _activeEncoder = "";
    public string ActiveEncoder
    {
        get => _activeEncoder;
        private set
        {
            if (!Set(ref _activeEncoder, value)) return;
            Notify(nameof(ExportMethodLabel));
        }
    }
    public string ExportMethodLabel => ActiveEncoder switch
    {
        "" or "Automatic" => "Choosing automatically...",
        "Software" or "Software (H.264)" => "Processor · Software",
        _ => "Graphics card · " + ActiveEncoder.Replace(" (hardware H.264)", ""),
    };
    private string _fallbackNotice = "";
    public string FallbackNotice { get => _fallbackNotice; private set => Set(ref _fallbackNotice, value); }
    private bool _canRetrySoftware;
    public bool CanRetrySoftware { get => _canRetrySoftware; set => Set(ref _canRetrySoftware, value); }

    public EncoderDetection? Evaluation => _detection;
    public bool IsListingHardware { get; private set; }

    public IEncoderStrategy CreateEncoder()
    {
        var adapter = SelectedAcceleration.Id == "automatic" ? _detection?.Preferred?.Adapter : SelectedAcceleration.Adapter;
        if (adapter is null && SelectedAcceleration.Id is not "automatic" and not "software")
            throw new HardwareExportException("The previously selected GPU is no longer available.");
        return adapter is null ? new LibX264EncoderStrategy() : new HardwareEncoderStrategy(adapter);
    }
    public string EncoderDescription => IsCheckingHardware ? "Evaluating export options..."
        : SelectedAcceleration.Id == "automatic" ? "Automatic uses cached results or evaluates options when export starts."
        : SelectedAcceleration.Adapter is not null ? "Will use: " + SelectedAcceleration.Name
        : SelectedAcceleration.Id == "software" ? "Will use: Software"
        : "This GPU is unavailable. Export will evaluate alternatives.";

    private void SetOptions(IEnumerable<HardwareAdapter> adapters, string? preferredId)
    {
        var previous = SelectedAcceleration;
        var id = preferredId ?? previous.Id;
        AccelerationOptions.Clear();
        AccelerationOptions.Add(ExportAccelerationOption.Automatic);
        foreach (var adapter in adapters.Where(a => a.Encoder is not null))
        {
            var result = _detection?.Results.FirstOrDefault(r => r.Adapter.Id == adapter.Id);
            AccelerationOptions.Add(new(adapter.Id, adapter.Name, adapter,
                result?.Available == true ? result.Milliseconds : null, result?.Available)
                { IsFastest = _detection?.Preferred?.Adapter.Id == adapter.Id });
        }
        AccelerationOptions.Add(ExportAccelerationOption.Software with
        { Milliseconds = _detection?.SoftwareMilliseconds, Available = _detection is null ? null : _detection.SoftwareMilliseconds is not null,
            IsFastest = _detection is { Preferred: null, SoftwareMilliseconds: not null } });
        var selected = AccelerationOptions.FirstOrDefault(o => o.Id == id);
        if (selected is null)
        {
            selected = new(id, previous.Id == id ? previous.Name : "Previously selected GPU (unavailable)");
            AccelerationOptions.Add(selected);
        }
        SelectedAcceleration = selected;
        Notify(nameof(EvaluationReport));
        Notify(nameof(CanOpenResults));
        Notify(nameof(HasFastest));
        Notify(nameof(FastestResult));
    }

    public async Task LoadOptionsAsync(HardwareEncoderDetection detector, string? preferredId, CancellationToken ct)
    {
        IsListingHardware = true;
        Notify(nameof(CanCheckHardware));
        try
        {
            IReadOnlyList<HardwareAdapter> adapters;
            try { adapters = await Task.Run(GraphicsAdapters.Enumerate, ct); }
            catch (System.Runtime.InteropServices.COMException) { adapters = Array.Empty<HardwareAdapter>(); }
            ct.ThrowIfCancellationRequested();
            _detection = await detector.ReadCachedAsync(ct);
            SetOptions(adapters, preferredId);
        }
        finally { IsListingHardware = false; Notify(nameof(CanCheckHardware)); }
    }

    public async Task CheckHardwareAsync(HardwareEncoderDetection detector, string? preferredId, bool recheck, CancellationToken ct)
    {
        if (IsCheckingHardware) return;
        IsCheckingHardware = true;
        try
        {
            _detection = await detector.DetectAsync(recheck, ct);
            ct.ThrowIfCancellationRequested();
            SetOptions(_detection.Results.Select(r => r.Adapter), preferredId);
            Notify(nameof(Evaluation));
            Notify(nameof(EvaluationReport));
        }
        finally { IsCheckingHardware = false; }
    }
}
