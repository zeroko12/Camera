using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Controls;
using System.Windows.Media;
using HandyControl.Controls;

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
                    element.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(Element_KeyDown), true);
                }
                else
                {
                    element.RemoveHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(Element_KeyDown));
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
                        element.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(Element_KeyDown), true);
                    }
                    else if (e.NewValue == null && e.OldValue != null)
                    {
                        element.RemoveHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(Element_KeyDown));
                    }
                }
            }
        }

        private static void Element_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && sender is DependencyObject d)
            {
                // 0. 特殊处理 HandyControl.Controls.NumericUpDown，确保内部 TextBox 输入的新文本已解析并设置到 Value
                if (d is HandyControl.Controls.NumericUpDown numericUpDown)
                {
                    var textBox = FindVisualChild<System.Windows.Controls.TextBox>(numericUpDown);
                    if (textBox != null)
                    {
                        if (string.IsNullOrWhiteSpace(textBox.Text))
                        {
                            numericUpDown.SetCurrentValue(HandyControl.Controls.NumericUpDown.ValueProperty, 0.0);
                        }
                        else if (double.TryParse(textBox.Text, out double val))
                        {
                            numericUpDown.SetCurrentValue(HandyControl.Controls.NumericUpDown.ValueProperty, val);
                        }
                        else
                        {
                            textBox.Text = numericUpDown.Value.ToString();
                        }
                    }
                }

                // 1. 先清除/转移焦点，迫使复合控件（如 NumericUpDown 内的 TextBox）触发 LostFocus 提交并更新 Value 属性
                if (GetClearFocusOnEnter(d) && d is FrameworkElement element)
                {
                    if (element.Focusable)
                    {
                        element.Focus();
                    }
                    else
                    {
                        DependencyObject parent = VisualTreeHelper.GetParent(element);
                        bool focused = false;
                        while (parent != null)
                        {
                            if (parent is FrameworkElement parentFe && parentFe.Focusable && !(parentFe is System.Windows.Window))
                            {
                                parentFe.Focus();
                                focused = true;
                                break;
                            }
                            parent = VisualTreeHelper.GetParent(parent);
                        }

                        if (!focused)
                        {
                            var focusScope = FocusManager.GetFocusScope(element);
                            FocusManager.SetFocusedElement(focusScope, null);
                            Keyboard.ClearFocus();
                        }
                    }
                }

                // 2. 遍历并强制更新当前控件上所有的双向数据绑定属性（此时 Value 已经是最新解析的值了）
                LocalValueEnumerator enumerator = d.GetLocalValueEnumerator();
                while (enumerator.MoveNext())
                {
                    LocalValueEntry entry = enumerator.Current;
                    if (BindingOperations.IsDataBound(d, entry.Property))
                    {
                        BindingExpression bindingExpression = BindingOperations.GetBindingExpression(d, entry.Property);
                        if (bindingExpression != null)
                        {
                            var mode = bindingExpression.ParentBinding.Mode;
                            if (mode == BindingMode.TwoWay || mode == BindingMode.Default || mode == BindingMode.OneWayToSource)
                            {
                                bindingExpression.UpdateSource();
                            }
                        }
                    }
                }

                // 3. 执行关联 Command
                ICommand command = GetEnterCommand(d);
                if (command != null)
                {
                    object parameter = GetEnterCommandParameter(d);
                    if (command.CanExecute(parameter))
                    {
                        command.Execute(parameter);
                    }
                }

                e.Handled = true;
            }
        }

        private static T FindVisualChild<T>(DependencyObject obj) where T : DependencyObject
        {
            if (obj == null) return null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(obj, i);
                if (child != null && child is T t)
                {
                    return t;
                }
                T childOfChild = FindVisualChild<T>(child);
                if (childOfChild != null)
                {
                    return childOfChild;
                }
            }
            return null;
        }
    }
}
