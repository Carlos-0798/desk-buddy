using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace DeskBuddy.App;

/// <summary>
/// Native WPF selection controls. Only the visual tree changes: ComboBox still owns
/// selection, keyboard navigation, focus, text search and its automation peer.
/// </summary>
internal static class ControlStyles
{
    public static void Register(ResourceDictionary resources)
    {
        var theme = (ResourceDictionary)XamlReader.Parse(SelectionControls);
        resources[typeof(ComboBox)] = theme[typeof(ComboBox)];
        resources[typeof(ComboBoxItem)] = theme[typeof(ComboBoxItem)];
    }

    // DIPs, rather than bitmaps or fixed pixel assets, preserve the design at
    // Windows DPI scales and the Hub's own layout scale. The popup width follows
    // the field so a long device name never grows beyond the available column.
    const string SelectionControls = """
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style TargetType="{x:Type ComboBoxItem}">
            <Setter Property="ToolTip" Value="{Binding}" />
            <Setter Property="Foreground" Value="#1C3540" />
            <Setter Property="Background" Value="Transparent" />
            <Setter Property="Padding" Value="11,8" />
            <Setter Property="MinHeight" Value="36" />
            <Setter Property="HorizontalContentAlignment" Value="Stretch" />
            <Setter Property="VerticalContentAlignment" Value="Center" />
            <Setter Property="FocusVisualStyle" Value="{x:Null}" />
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="{x:Type ComboBoxItem}">
                  <Border x:Name="Row" Margin="3,1" Padding="{TemplateBinding Padding}"
                          CornerRadius="6" Background="{TemplateBinding Background}"
                          BorderBrush="Transparent" BorderThickness="1">
                    <Grid>
                      <Grid.ColumnDefinitions><ColumnDefinition Width="*" /><ColumnDefinition Width="23" /></Grid.ColumnDefinitions>
                      <ContentPresenter VerticalAlignment="Center" HorizontalAlignment="Stretch"
                                        RecognizesAccessKey="True" />
                      <Path x:Name="Check" Grid.Column="1" HorizontalAlignment="Right" VerticalAlignment="Center"
                            Data="M 0,4 L 3,7 L 9,0" Stroke="#147E70" StrokeThickness="1.8"
                            StrokeStartLineCap="Round" StrokeEndLineCap="Round" Visibility="Hidden" />
                    </Grid>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsSelected" Value="True">
                      <Setter TargetName="Row" Property="Background" Value="#E6F5F0" />
                      <Setter Property="Foreground" Value="#126B5F" />
                      <Setter TargetName="Check" Property="Visibility" Value="Visible" />
                    </Trigger>
                    <Trigger Property="IsHighlighted" Value="True">
                      <Setter TargetName="Row" Property="Background" Value="#EFF7F5" />
                      <Setter TargetName="Row" Property="BorderBrush" Value="#A8CEC4" />
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False">
                      <Setter Property="Foreground" Value="#7E8D93" />
                      <Setter TargetName="Row" Property="Opacity" Value="0.65" />
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style TargetType="{x:Type ComboBox}">
            <Setter Property="ToolTip" Value="{Binding SelectedItem, RelativeSource={RelativeSource Self}}" />
            <Setter Property="Foreground" Value="#1C3540" />
            <Setter Property="Background" Value="White" />
            <Setter Property="BorderBrush" Value="#D7E4E7" />
            <Setter Property="BorderThickness" Value="1" />
            <Setter Property="Padding" Value="12,9,36,9" />
            <Setter Property="MinHeight" Value="40" />
            <Setter Property="HorizontalContentAlignment" Value="Stretch" />
            <Setter Property="VerticalContentAlignment" Value="Center" />
            <Setter Property="MaxDropDownHeight" Value="340" />
            <Setter Property="ScrollViewer.HorizontalScrollBarVisibility" Value="Disabled" />
            <Setter Property="ScrollViewer.VerticalScrollBarVisibility" Value="Auto" />
            <Setter Property="ScrollViewer.CanContentScroll" Value="True" />
            <Setter Property="FocusVisualStyle" Value="{x:Null}" />
            <Setter Property="SnapsToDevicePixels" Value="True" />
            <Setter Property="UseLayoutRounding" Value="True" />
            <Setter Property="ItemTemplate">
              <Setter.Value>
                <DataTemplate><TextBlock Text="{Binding}" TextTrimming="CharacterEllipsis" TextWrapping="NoWrap" VerticalAlignment="Center" /></DataTemplate>
              </Setter.Value>
            </Setter>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="{x:Type ComboBox}">
                  <Grid x:Name="Field" SnapsToDevicePixels="True">
                    <Border x:Name="Frame" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="8" />
                    <ToggleButton x:Name="DropDownToggle" Focusable="False" IsTabStop="False" ClickMode="Press"
                                  Background="Transparent" BorderThickness="0"
                                  IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
                      <ToggleButton.Template>
                        <ControlTemplate TargetType="{x:Type ToggleButton}">
                          <Border Background="Transparent" CornerRadius="8">
                            <Path x:Name="Chevron" Data="M 0,0 L 4,4 L 8,0" Margin="0,0,14,0"
                                  Stroke="#60747D" StrokeThickness="1.6" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
                                  HorizontalAlignment="Right" VerticalAlignment="Center" />
                          </Border>
                          <ControlTemplate.Triggers>
                            <Trigger Property="IsChecked" Value="True"><Setter TargetName="Chevron" Property="Stroke" Value="#147E70" /></Trigger>
                          </ControlTemplate.Triggers>
                        </ControlTemplate>
                      </ToggleButton.Template>
                    </ToggleButton>
                    <ContentPresenter x:Name="Selection" IsHitTestVisible="False" Margin="{TemplateBinding Padding}"
                                      Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"
                                      ContentTemplateSelector="{TemplateBinding ItemTemplateSelector}"
                                      ContentStringFormat="{TemplateBinding SelectionBoxItemStringFormat}"
                                      HorizontalAlignment="Stretch" VerticalAlignment="Center" />
                    <Popup x:Name="PART_Popup" Placement="Bottom" VerticalOffset="5" AllowsTransparency="True" Focusable="False"
                           IsOpen="{TemplateBinding IsDropDownOpen}" PopupAnimation="None">
                      <Border x:Name="PopupFrame" Background="White" BorderBrush="#C6DAD7" BorderThickness="1" CornerRadius="9"
                              Padding="3" Width="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}"
                              MaxHeight="{TemplateBinding MaxDropDownHeight}" SnapsToDevicePixels="True">
                        <ScrollViewer CanContentScroll="True" HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto"
                                      Focusable="False" KeyboardNavigation.DirectionalNavigation="Contained">
                          <ItemsPresenter x:Name="ItemsPresenter" KeyboardNavigation.DirectionalNavigation="Contained" />
                        </ScrollViewer>
                      </Border>
                    </Popup>
                  </Grid>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="Frame" Property="Background" Value="#F4FAF8" />
                      <Setter TargetName="Frame" Property="BorderBrush" Value="#9FC7BE" />
                    </Trigger>
                    <Trigger Property="IsKeyboardFocusWithin" Value="True">
                      <Setter TargetName="Frame" Property="BorderBrush" Value="#147E70" />
                      <Setter TargetName="Frame" Property="BorderThickness" Value="2" />
                    </Trigger>
                    <Trigger Property="IsDropDownOpen" Value="True">
                      <Setter TargetName="Frame" Property="BorderBrush" Value="#147E70" />
                      <Setter TargetName="Frame" Property="Background" Value="#F4FAF8" />
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False">
                      <Setter Property="Foreground" Value="#74848B" />
                      <Setter TargetName="Frame" Property="Background" Value="#F1F5F6" />
                      <Setter TargetName="Frame" Property="BorderBrush" Value="#E2E9EB" />
                      <Setter TargetName="DropDownToggle" Property="Opacity" Value="0.45" />
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
        </ResourceDictionary>
        """;
}
