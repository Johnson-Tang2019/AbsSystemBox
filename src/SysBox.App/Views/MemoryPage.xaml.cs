// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using SysBox.Presentation;

namespace SysBox.App.Views;

public sealed partial class MemoryPage : Page, IPageLifetime
{
    public DashboardViewModel ViewModel { get; } = App.Services.GetRequiredService<DashboardViewModel>();
    public MemoryPage()
    {
        InitializeComponent();
        DataContext = ViewModel;
        Unloaded += (_, _) => Deactivate();
    }
    public void Deactivate() => ViewModel.Deactivate();
}
