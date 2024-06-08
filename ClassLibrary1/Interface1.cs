using AttributeGenerator;

namespace ClassLibrarySecond;
[AutoImplement]
public interface IUserInterface
{
    public int InterfaceProperty { get; set; }
    public float InterfacePropertyOnlyReadonly { get; }
}