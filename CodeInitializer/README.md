# C# Code Initializer

A Visual Studio extension to instantly generate interfaces from your C# classes and quickly initialize all properties in object initializers. Designed for productivity, best practices, and seamless workflow.

---

## ✨ Features

- **Generate Interfaces from Classes**
  - Create interfaces from any C# class via context menu or code action.
  - Supports generic classes and methods.
  - Choose whether to include or exclude generic members.
  - Interfaces accurately mirror class type parameters (e.g., `IMyClass<T, TBody>` for `MyClass<T, TBody>`).
  - Automatically adds the generated interface to your class declaration.

- **Property Initializer Refactoring**
  - Place your cursor inside an object initializer (e.g., `var s = new Person { }`) and use the code action to insert assignments for all public properties.
  - Perfect for rapid setup of DTOs, ViewModels, and test data.

- **Modern & Reliable**
  - Built with the Roslyn compiler platform for robust, syntax-aware transformations.
  - Designed for Visual Studio 2022 and later.

> **Note:** Partial classes are not fully supported; only a partial interface will be generated if used.

---

## 🚀 Getting Started

1. **Install the Extension** from the Visual Studio Marketplace.
2. **Generate an Interface:**  
   - Right-click a class in your C# code or use the lightbulb/code action menu.
   - Select “Generate Interface.”  
   - Optionally choose to include generic members.
3. **Initialize Properties in Object Initializer:**  
   - Place your cursor inside the `{}` of an object initializer.
   - Trigger the code action (lightbulb menu) and select “Initialize all properties.”

---

## 📝 License

[MIT License](https://opensource.org/licenses/MIT)

---

## 💡 Feedback & Contributions

Feedback, bug reports, and feature requests are welcome via the [GitHub issues page](https://github.com/sander1993s/SSS.CodeInitializer/issues) or the Visual Studio Marketplace.  
Enjoy faster, cleaner C# code!

