# Changelog

## [1.0.0] - 2025-05-29

### 🚀 Initial Release

#### Features

- **Generate Interfaces from Classes**
  - Create interfaces from any C# class using a context menu or code action.
  - Supports classes with generic type parameters (`class MyClass<T>`) and generic methods.
  - Optionally include or exclude generic members in the generated interface.
  - Generated interfaces mirror the class’s type parameters (e.g., `IMyClass<T, TBody>` for `MyClass<T, TBody>`).
  - Automatically adds the generated interface to the class declaration.

- **Property Initializer Refactoring**
  - Within an object initializer (e.g., `var s = new Person { }`), trigger a code action to automatically insert assignments for all public properties of the class.
  - Great for fast DTO, ViewModel, or test object construction.

#### Other Details

- Powered by the Roslyn compiler platform for syntax-aware and reliable code transformations.
- Designed for Visual Studio 2022 and later.
- **Partial classes are not fully supported** and only generate a partial interface in this version.

---

**Thank you for using this extension!  
Feedback, bug reports, and feature requests are welcome.**
