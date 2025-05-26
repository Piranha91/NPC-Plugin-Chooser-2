using NPC_Plugin_Chooser_2.View_Models;
using ReactiveUI;
using System;
using System.Reactive;
using System.Reactive.Disposables;
using System.Windows;

namespace NPC_Plugin_Chooser_2.Views
{
    public partial class SplashScreenWindow : Window, IViewFor<VM_SplashScreen>
    {
        public SplashScreenWindow()
        {
            InitializeComponent();

            this.WhenActivated(disposables =>
            {
                ViewModel?.RequestOpen.RegisterHandler(_ =>
                {
                    this.Dispatcher.Invoke(Show);
                    _.SetOutput(Unit.Default);
                }).DisposeWith(disposables);

                ViewModel?.RequestClose.RegisterHandler(_ =>
                {
                    this.Dispatcher.Invoke(Close);
                    _.SetOutput(Unit.Default);
                }).DisposeWith(disposables);
            });
        }

        public VM_SplashScreen? ViewModel
        {
            get => (VM_SplashScreen?)DataContext;
            set => DataContext = value;
        }

        object? IViewFor.ViewModel
        {
            get => ViewModel;
            set => ViewModel = (VM_SplashScreen?)value;
        }
    }
}