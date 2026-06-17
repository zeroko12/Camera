using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Data;

namespace camera.Infrastructure
{
    /// <summary>
    /// 提供对各类输入控件（TextBox, NumericUpDown 等）回车确认行为支持的附加属性。
    /// </summary>
    public static class InputBehavior
    {
        // ═══ 1. UpdateOnEnter 附加属性 ═══
        public static readonly DependencyProperty UpdateOnEnterProperty =
            DependencyProperty.RegisterAttached(
                "UpdateOnEnter",
                typeof(bool),
                typeof(InputBehavior),
                new PropertyMetadata(false, OnUpdateOnEnterChanged));

        public static bool GetUpdateOnEnter(DependencyObject obj) => (bool)obj.GetValue(UpdateOnEnterProperty);
        public static void SetUpdateOnEnter(DependencyObject obj, bool value) => obj.SetValue(UpdateOnEnterProperty, value);

        // ═══ 2. EnterCommand 附加属性 ═══
        public static readonly DependencyProperty EnterCommandProperty =
            DependencyProperty.RegisterAttached(
                "EnterCommand",
                typeof(ICommand),
                typeof(InputBehavior),
                new PropertyMetadata(null, OnEnterCommandChanged));

        public static ICommand GetEnterCommand(DependencyObject obj) => (ICommand)obj.GetValue(EnterCommandProperty);
        public static void SetEnterCommand(DependencyObject obj, ICommand value) => obj.SetValue(EnterCommandProperty, value);

        // ═══ 3. EnterCommandParameter 附加属性 ═══
        public static readonly DependencyProperty EnterCommandParameterProperty =
            DependencyProperty.RegisterAttached(
                "EnterCommandParameter",
                typeof(object),
                typeof(InputBehavior),
                new PropertyMetadata(null));

        public static object GetEnterCommandParameter(DependencyObject obj) => obj.GetValue(EnterCommandParameterProperty);
        public static void SetEnterCommandParameter(DependencyObject obj, object value) => obj.SetValue(EnterCommandParameterProperty, value);

        // ═══ 4. ClearFocusOnEnter 附加属性 ═══
        public static readonly DependencyProperty ClearFocusOnEnterProperty =
            DependencyProperty.RegisterAttached(
                "ClearFocusOnEnter",
                typeof(bool),
                typeof(InputBehavior),
                new PropertyMetadata(true));

        public static bool GetClearFocusOnEnter(DependencyObject obj) => (bool)obj.GetValue(ClearFocusOnEnterProperty);
        public static void SetClearFocusOnEnter(DependencyObject obj, bool value) => obj.SetValue(ClearFocusOnEnterProperty, value);


        private static void OnUpdateOnEnterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is UIElement element)
            {
                if ((bool)e.NewValue)
                {
                    element.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Element_KeyDown), true);
                }
                else
                {
                    element.RemoveHandler(UIElement.KeyDownEvent, new KeyEventHandler(Element_KeyDown));
                }
            }
        }

        private static void OnEnterCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is UIElement element)
            {
                if (!GetUpdateOnEnter(element))
                {
                    if (e.NewValue != null && e.OldValue == null)
                    {
                        element.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Element_KeyDown), true);
                    }
                    else if (e.NewValue == null && e.OldValue != null)
                    {
                        element.RemoveHandler(UIElement.KeyDownEvent, new KeyEventHandler(Element_KeyDown));
                    }
                }
            }
        }

        private static void Element_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && sender is DependencyObject d)
            {
                // 1. 遍历并强制更新当前控件上所有的双向数据绑定属性（如 TextBox.Text, NumericUpDown.Value 等）
                LocalValueEnumerator enumerator = d.GetLocalValueEnumerator();
                while (enumerator.MoveNext())
                {
                    LocalValueEntry entry = enumerator.Current;
                    if (BindingOperations.IsDataBound(d, entry.Property))
                    {
                        BindingExpression bindingExpression = BindingOperations.GetBindingExpression(d, entry.Property);
                        if (bindingExpression != null && bindingExpression.ParentBinding.Mode == BindingMode.TwoWay)
                        {
                            bindingExpression.UpdateSource();
                        }
                    }
                }

                // 2. 执行关联 Command
                ICommand command = GetEnterCommand(d);
                if (command != null)
                {
                    object parameter = GetEnterCommandParameter(d);
                    if (command.CanExecute(parameter))
                    {
                        command.Execute(parameter);
                    }
                }

                // 3. 清除焦点
                if (GetClearFocusOnEnter(d) && d is FrameworkElement element)
                {
                    FrameworkElement parent = element.Parent as FrameworkElement;
                    if (parent != null)
                    {
                        parent.Focus();
                    }
                    else
                    {
                        Keyboard.ClearFocus();
                    }
                }

                e.Handled = true;
            }
        }
    }
}
