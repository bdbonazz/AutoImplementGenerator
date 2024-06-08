using ClassLibrary1;

namespace WebApplication1;

public class WeatherForecast
{
    public DateOnly Date { get; set; }

    public int TemperatureC { get; set; }

    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);

    public string? Summary { get; set; }

    public void ciao()
    {
        UserClass class1 = new UserClass();
        class1.Prop32 = new UserClass();
    }
}
