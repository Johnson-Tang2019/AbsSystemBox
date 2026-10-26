// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SysBox.Presentation;
namespace SysBox.App.Controls;

public sealed partial class BatteryCard : UserControl
{
    private readonly BatteryViewModel model = App.Services.GetRequiredService<BatteryViewModel>();
    public BatteryCard()
    {
        InitializeComponent(); DataContext=model;
        model.PropertyChanged += (_,e)=> { if(e.PropertyName == nameof(model.IsCharging)) Card.Visibility=model.IsCharging ? Visibility.Visible : Visibility.Collapsed; };
        Loaded += (_,_)=>model.Start();
        Unloaded += (_,_)=>model.Deactivate();
    }
}
