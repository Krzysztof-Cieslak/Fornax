![Logo](https://raw.githubusercontent.com/LambdaFactory/Fornax/master/logo/Fornax.png)

Fornax is a static site generator using type safe F# DSL to define page templates.

## Working features

* Defining templates in F# DSL
* Creating pages using templates from `.md` files with `layout` entry
* Creating plain pages without templates from `md` files without `layout` entry
* Transforming `.less` files to `.css` files
* Transforming `.scss` files to `.css` files (require having `sass` installed)
* Coping other static content to output directory

## Planned features

* Defining `.css` styles using F# DSL
* Handling site settings defined in multiple files (a la Jekyll's `_data` folder) (multiple models? unified model?)

## Installation.

Fornax is released as a global .Net Core tool. You can install it with `dotnet tool install fornax -g`

## CLI Application

The main functionality of Fornax comes from CLI applications that lets user scaffold, and generate webpages.

* `fornax new` - scaffolds new blog in current working directory using really simple template
* `fornax build` - builds webpage, puts output to `_public` folder
* `fornax watch` - starts small webserver that host your blog post, and background process that recompiles blog whenever any changes are detected. That's suggested way of working with Fornax
* `fornax clean` - removes output directory and any temp files.
* `fornax version` - prints out version of Fornax
* `fornax help` - prints out help

## Website definition

Fornax is using normal F# code (F# script files) to define templates and data types representing content, and yaml and Markdown to provide content (fitting defined models) for the templates. Sample webpage can be found in `samples` folder - to build sample webpage run `fornax build` in this folder.

### Site Settings

Site settings are information passed to every page during generation - every template has access to this data.

The model representing site settings is defined in `siteModel.fsx` file in the root folder of the webpage, content of settings is defined in `_config.yml` file in the root folder of the webpage.

Sample `siteModel.fsx`:

```
type SiteModel = {
    SomeGlobalValue : string
}
```

Sample `_config.yml`:
```
SomeGlobalValue: "Test global value"
```

### Templates

Templates are F# script files representing different templates that can be used in the website. They need to `#load` `siteModel.fsx` file, and `#r` `Fornax.Core.dll`. They need to define F# record called `Model` which defines additional settings passed to this particular template, and `generate` function of following signature: `SiteModel -> Model -> Post list -> string -> HtmlElement`. `SiteModel` is type representing global settings of webpage, `Model` is type representing settings for this template, `Post list` contains simplified information about all available posts in the blog (useful for navigation, creating tag cloud etc.) `string` is main content of post (already compiled to `html`).

Templates are defined using DSL defined in `Html` module of `Fornax.Core`.

All templates should be defined in `templates` folder.

Sample template:
```
#r "../lib/Fornax.Core.dll"
#load "../siteModel.fsx"

open Html
open SiteModel

type Model = {
    Name : string
    Surname : string
}

let generate (siteModel : SiteModel) (mdl : Model) (posts: Post list) (content : string) =
    html [] [
        div [] [
            span [] [ !! ("Hello world " + mdl.Name) ]
            span [] [ !! content ]
            span [] [ !! siteModel.SomeGlobalValue ]
        ]
    ]
```

### Page content

Content files are `.md` files containing page content, and header with settings (defined using yaml). Header part is parsed, and passed to template `generate` function as `Model`. Content part is compiled to html and also passed to `generate` function. Header part needs to have `layout` entry which defines which template will be used for the page.

Sample page:

```
---
layout: post
Name: Ja3
Surname: Ja4
---
# Something else

Some blog post written in Markdown
```

### Post list

Templates are getting `Post list` as one of the input parameter that can be used for navigation, creating tag clouds etc. The `Post` is a record of following structure:
```
type Post = {
    link : string
    title: string
    author: string option
    published: System.DateTime option
    tags: string list
    content: string
}
```
It's filled based on respective entries in `layout` part of the post content file. `link` is using name of the file - it's usually something like `\posts\post1.html`


## FAQ

1. Hmmm... it looks similar to Jekyll, doesn't it?

* Yes, indeed. But the main advantage over Jekyll is type safe DSL for defining templates, and fact it's using normal programming language - no additional syntax to things like loops or conditional statements, it's also very easy to compose templates - you just `#load` other template and execute as normal F# function

2. What about F# Formatting?

* F# Formatting is really good project, but it doesn't provide own rendering / templating engine - it's using Razor for that. Fornax right now is handling *only* rendering / templating - hopefully, it should work pretty well as rendering engine for F# Formatting.

## How to contribute

*Imposter syndrome disclaimer*: I want your help. No really, I do.

There might be a little voice inside that tells you you're not ready; that you need to do one more tutorial, or learn another framework, or write a few more blog posts before you can help me with this project.

I assure you, that's not the case.

This project has some clear Contribution Guidelines and expectations that you can [read here](https://github.com/LambdaFactory/Saturn/blob/master/CONTRIBUTING.md).

The contribution guidelines outline the process that you'll need to follow to get a patch merged. By making expectations and process explicit, I hope it will make it easier for you to contribute.

And you don't just have to write code. You can help out by writing documentation, tests, or even by giving feedback about this work. (And yes, that includes giving feedback about the contribution guidelines.)

Thank you for contributing!

## Contributing and copyright

The project is hosted on [GitHub](https://github.com/LambdaFactory/Fornax) where you can [report issues](https://github.com/LambdaFactory/Fornax/issues), fork
the project and submit pull requests. Please read [Contribution Guide](https://github.com/LambdaFactory/Fornax/blob/master/CONTRIBUTING.md)

The library is available under [MIT license](https://github.com/LambdaFactory/Fornax/blob/master/LICENSE.md), which allows modification and redistribution for both commercial and non-commercial purposes.
