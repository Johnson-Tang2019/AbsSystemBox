// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using SysBox.Abstractions;

namespace SysBox.Presentation;

public sealed class PeripheralsViewModel(IPeripheralDiscovery discovery) : ObservableObject
{
    private readonly Dictionary<PeripheralKind, string> selections = [];
    private CancellationTokenSource? pending;

    public IReadOnlyList<PeripheralDevice> Devices { get; private set; } = [];
    public IReadOnlyList<PeripheralDevice> DevicesInSelectedKind { get; private set; } = [];
    public PeripheralKind? SelectedKind { get; private set; }
    public PeripheralDevice? SelectedDevice { get; private set; }
    public bool IsSelectedDeviceMissing { get; private set; }
    public bool IsRefreshing { get; private set; }
    public bool HasLoaded { get; private set; }
    public string? ErrorMessage { get; private set; }

    public void ShowOverview()
    {
        if (SelectedKind is null)
            return;

        SelectedKind = null;
        UpdateSelection();
        NotifySelection();
    }

    public void ShowKind(PeripheralKind kind)
    {
        if (SelectedKind == kind)
            return;

        SelectedKind = kind;
        UpdateSelection();
        NotifySelection();
    }

    public void ShowDevice(PeripheralDevice device)
    {
        if (SelectedKind == device.Kind && SelectedDevice?.Id == device.Id)
            return;
        if (!Devices.Any(candidate => candidate.Id == device.Id && candidate.Kind == device.Kind))
            return;

        selections[device.Kind] = device.Id;
        SelectedKind = device.Kind;
        UpdateSelection();
        NotifySelection();
    }

    public Task LoadIfNeededAsync() =>
        HasLoaded && ErrorMessage is null && !Devices.Any(NeedsMouseReadout)
            ? Task.CompletedTask : RefreshAsync();

    private static bool NeedsMouseReadout(PeripheralDevice device) =>
        device.Kind == PeripheralKind.Mouse && device.MouseReadout?.Performance is null &&
        string.Equals(device.ReceiverName, "MCHOSE K7 Ultra", StringComparison.OrdinalIgnoreCase);

    public void UpdateConfirmedDevice(PeripheralDevice device)
    {
        if (!Devices.Any(current => current.Id == device.Id && current.Kind == device.Kind))
            return;
        Devices = Devices.Select(current => current.Id == device.Id ? device : current).ToArray();
        UpdateSelection();
        NotifySnapshot();
    }

    public async Task RefreshAsync()
    {
        if (pending is not null)
            return;

        using var request = new CancellationTokenSource();
        pending = request;
        IsRefreshing = true;
        OnPropertyChanged(nameof(IsRefreshing));
        try
        {
            var result = await discovery.DiscoverAsync(request.Token);
            if (!ReferenceEquals(pending, request))
                return;

            Devices = result.IsSuccess ? result.Value : [];
            ErrorMessage = result.Error?.Message;
            HasLoaded = true;
            UpdateSelection();
            NotifySnapshot();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(pending, request))
            {
                pending = null;
                IsRefreshing = false;
                OnPropertyChanged(nameof(IsRefreshing));
            }
        }
    }

    public void Deactivate()
    {
        var request = pending;
        pending = null;
        if (request is not null)
        {
            request.Cancel();
            IsRefreshing = false;
            OnPropertyChanged(nameof(IsRefreshing));
        }
        selections.Clear();
        ShowOverview();
    }

    private void UpdateSelection()
    {
        DevicesInSelectedKind = [];
        SelectedDevice = null;
        IsSelectedDeviceMissing = false;
        if (SelectedKind is not { } kind)
            return;

        DevicesInSelectedKind = Devices.Where(device => device.Kind == kind).ToArray();
        if (selections.TryGetValue(kind, out var id))
        {
            SelectedDevice = DevicesInSelectedKind.FirstOrDefault(device => device.Id == id);
            IsSelectedDeviceMissing = HasLoaded && ErrorMessage is null && SelectedDevice is null;
        }
        else if (DevicesInSelectedKind.Count == 1)
        {
            SelectedDevice = DevicesInSelectedKind[0];
            selections[kind] = SelectedDevice.Id;
        }
    }

    private void NotifySnapshot()
    {
        OnPropertyChanged(nameof(Devices));
        OnPropertyChanged(nameof(HasLoaded));
        OnPropertyChanged(nameof(ErrorMessage));
        NotifySelection();
    }

    private void NotifySelection()
    {
        OnPropertyChanged(nameof(SelectedKind));
        OnPropertyChanged(nameof(DevicesInSelectedKind));
        OnPropertyChanged(nameof(SelectedDevice));
        OnPropertyChanged(nameof(IsSelectedDeviceMissing));
    }
}
